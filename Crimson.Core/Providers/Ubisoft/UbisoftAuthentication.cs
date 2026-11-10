using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Serilog;

namespace Crimson.Providers.Ubisoft;

public interface IUbisoftSessionStore
{
    Task<UbisoftSession?> Load(CancellationToken token);
    Task Save(UbisoftSession session, CancellationToken token);
    Task Delete();
}

public sealed class UbisoftSessionStore(string providerRoot, ICredentialProtector protector) : IUbisoftSessionStore
{
    private readonly string _file = Path.Combine(providerRoot, "session.dat");
    public async Task<UbisoftSession?> Load(CancellationToken token) => File.Exists(_file)
        ? JsonSerializer.Deserialize<UbisoftSession>(protector.Unprotect(await File.ReadAllTextAsync(_file, token))) : null;
    public async Task Save(UbisoftSession session, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, protector.Protect(JsonSerializer.Serialize(session)), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task Delete()
    {
        if (File.Exists(_file)) File.Delete(_file);
        return Task.CompletedTask;
    }
}

public sealed class UbisoftAuthentication(UbisoftApi api, IUbisoftSessionStore store,
    UbisoftSignInCredentialStore? credentials = null, ILogger? log = null) : ILauncherAuthentication
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _twoFactorChallenge;
    private bool _remember;
    private bool _keepStoredSession;
    private CancellationTokenSource? _renewal;
    private Task? _renewalLoop;

    /// <summary>Extend this long before expiry so a running session never lapses.</summary>
    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(30);

    /// <summary>How often the renewing loop wakes up; the official client extends periodically too.</summary>
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(10);
    public UbisoftSession? Session { get; private set; }
    public AuthenticationStatus AuthenticationStatus { get; private set; } = AuthenticationStatus.LoggedOut;
    public event Action<AuthenticationStatus>? StatusChanged;
    private void Publish(AuthenticationStatus status) { AuthenticationStatus = status; StatusChanged?.Invoke(status); }

    /// <returns>True when a two-factor code is required. Passwords are never saved.</returns>
    public async Task<bool> SignIn(string email, string password, bool remember, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _twoFactorChallenge = null;
            var json = await api.Password(email, password, remember, token);
            if (json.TryGetProperty("twoFactorAuthenticationTicket", out var challenge) && !string.IsNullOrEmpty(challenge.GetString()))
            {
                _twoFactorChallenge = challenge.GetString();
                _remember = remember;
                return true;
            }
            // Staying signed in needs the sign-in itself: Ubisoft's ticket lasts hours and its
            // remember-me renewal rejects third-party clients.
            if (remember) credentials?.Save(new UbisoftSignInCredential(email.Trim(), password));
            else credentials?.Delete();
            await Adopt(UbisoftSession.Parse(json, remember) with { Email = email.Trim() }, remember, token);
            log?.Information("Ubisoft: signed in; account remembered={Remembered}", remember);
            return false;
        }
        finally { _gate.Release(); }
    }
    public async Task CompleteTwoFactor(string code, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_twoFactorChallenge is null) throw new InvalidOperationException("Start sign-in before submitting a code.");
            var json = await api.TwoFactor(_twoFactorChallenge, code, token);
            var pending = Session?.Email ?? ""; // Carried from the password step, where the address is known.
            await Adopt(UbisoftSession.Parse(json, _remember) with { Email = pending }, _remember, token);
            _twoFactorChallenge = null;
        }
        finally { _gate.Release(); }
    }
    public void CancelSignIn() => _twoFactorChallenge = null;
    public Task<LauncherUser?> GetUser(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var session = Session;
        return Task.FromResult(session is null ? null
            : new LauncherUser(string.IsNullOrWhiteSpace(session.Name) ? null : session.Name,
                string.IsNullOrWhiteSpace(session.UserId) ? null : session.UserId));
    }

    public async Task<AuthenticationStatus> CheckAuthStatus()
    {
        await _gate.WaitAsync();
        try
        {
            Publish(AuthenticationStatus.Checking);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var session = Session ?? await store.Load(timeout.Token);
            if (session is null)
            {
                log?.Information("Ubisoft: no stored session; remembered sign-in {Remembered}",
                    credentials?.Exists == true ? "available" : "missing");
                // Nothing usable on disk, but a remembered sign-in can still restore the session.
                if (await TrySilentSignInAsync(timeout.Token)) return AuthenticationStatus;
                Publish(AuthenticationStatus.LoggedOut);
                return AuthenticationStatus;
            }
            // A stored session is the remembered identity; only an explicit disconnect forgets it.
            _keepStoredSession = true;
            LastEmail = session.Email;
            log?.Information("Ubisoft: stored session found, expires in {Minutes:0} minutes",
                (session.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes);
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                // Still valid: extend it now so the next launch finds a live ticket.
                var extended = UbisoftSession.Parse(await api.Extend(session, true, timeout.Token), true)
                    with { Email = session.Email };
                await Adopt(extended, true, timeout.Token);
                log?.Information("Ubisoft: session extended at launch");
                return AuthenticationStatus;
            }
            if (session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                // Renewal is expected to fail: Ubisoft answers 401 "Ticket is invalid" to
                // third-party clients. Keep the stored session and ask for the password again.
                if (string.IsNullOrEmpty(session.RememberMeTicket))
                {
                    Session = null;
                    Publish(AuthenticationStatus.LoginFailed);
                    return AuthenticationStatus;
                }
                session = UbisoftSession.Parse(await api.Remember(session.RememberMeTicket, timeout.Token), true)
                    with { Email = session.Email };
            }
            await api.Get(session, "/v3/users/me", timeout.Token);
            await Adopt(session, true, timeout.Token);
        }
        catch (UbisoftApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The ticket is no longer accepted. Reconnect silently when the person asked to stay
            // signed in; otherwise keep the stored session so the next sign-in is pre-filled.
            Session = null;
            log?.Information("Ubisoft: stored ticket refused ({Status}); replaying the remembered sign-in", ex.Status);
            if (await TrySilentSignInAsync(CancellationToken.None)) return AuthenticationStatus;
            log?.Warning("Ubisoft: could not reconnect automatically; sign-in required");
            Publish(AuthenticationStatus.LoginFailed);
        }
        catch (Exception) { Session = null; Publish(AuthenticationStatus.LoginFailed); }
        finally { _gate.Release(); }
        return AuthenticationStatus;
    }
    /// <summary>
    /// Reconnects with the stored sign-in, the way the official client does after its ticket dies.
    /// A challenge or rejection is not an error to report: it simply leaves the person to sign in.
    /// </summary>
    private async Task<bool> TrySilentSignInAsync(CancellationToken token)
    {
        if (credentials?.Load() is not { } stored)
        {
            log?.Information("Ubisoft: no remembered sign-in to replay");
            return false;
        }
        LastEmail = stored.Email;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var json = await api.Password(stored.Email, stored.Password, true, timeout.Token);
            if (json.TryGetProperty("twoFactorAuthenticationTicket", out var challenge) &&
                !string.IsNullOrEmpty(challenge.GetString()))
            {
                // Silent reconnection cannot answer a code; the dialog completes it.
                _twoFactorChallenge = challenge.GetString();
                _remember = true;
                return false;
            }
            var session = UbisoftSession.Parse(json, true) with { Email = stored.Email };
            _keepStoredSession = true;
            await Adopt(session, true, timeout.Token);
            log?.Information("Ubisoft: reconnected with the remembered sign-in");
            return true;
        }
        catch (Exception error) when (error is UbisoftApiException or System.Net.Http.HttpRequestException
            or OperationCanceledException)
        {
            log?.Warning("Ubisoft: remembered sign-in was refused ({ErrorType})", error.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Keeps a running session alive by extending it before it expires, the way the official client
    /// does. Nothing is written to disk beyond the refreshed ticket.
    /// </summary>
    private void StartRenewal()
    {
        if (_renewalLoop is { IsCompleted: false }) return;
        _renewal?.Cancel();
        _renewal = new CancellationTokenSource();
        var token = _renewal.Token;
        _renewalLoop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(RenewInterval, token); }
                catch (OperationCanceledException) { return; }
                try { await ExtendIfNeededAsync(token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { /* The next tick retries; the stored session is untouched. */ }
            }
        }, token);
    }

    private void StopRenewal()
    {
        _renewal?.Cancel();
        _renewal = null;
        _renewalLoop = null;
    }

    /// <summary>Extends the session when it is close to expiry; a no-op while it has time left.</summary>
    public async Task ExtendIfNeededAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var session = Session;
            if (session is null || session.ExpiresAt - DateTimeOffset.UtcNow > RenewAhead) return;
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) return; // Too late: only a new sign-in helps.
            var extended = UbisoftSession.Parse(await api.Extend(session, _keepStoredSession, token), _keepStoredSession)
                with { Email = session.Email };
            await Adopt(extended, _keepStoredSession || !string.IsNullOrEmpty(extended.RememberMeTicket), token);
            log?.Information("Ubisoft: renewal extended the session");
        }
        catch (UbisoftApiException error)
        {
            // Keep the current ticket; the loop tries again before it lapses.
            log?.Warning("Ubisoft: renewal refused ({Status}); keeping the current ticket", error.Status);
        }
        catch (Exception error)
        {
            log?.Warning("Ubisoft: renewal could not reach the service ({ErrorType})", error.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Address of the last remembered account, for prefilling the sign-in dialog.</summary>
    public string LastEmail { get; private set; } = "";

    public async Task<UbisoftSession> RequireSession(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Session is null || Session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2)) await CheckAuthStatus();
        token.ThrowIfCancellationRequested();
        return Session ?? throw new InvalidOperationException("Connect your Ubisoft account again.");
    }
    private async Task Adopt(UbisoftSession session, bool remember, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Persist when asked to, or when a session was already stored and is merely being refreshed.
        if (remember || _keepStoredSession) await store.Save(session, token);
        else await store.Delete();
        _keepStoredSession = remember;
        LastEmail = session.Email;
        Session = session;
        Publish(AuthenticationStatus.LoggedIn);
        StartRenewal();
    }
    public async Task Logout()
    {
        await _gate.WaitAsync();
        try
        {
            StopRenewal();
            _keepStoredSession = false;
            LastEmail = "";
            credentials?.Delete();
            await Clear();
            log?.Information("Ubisoft: disconnected and forgot the stored session");
        }
        finally { _gate.Release(); }
    }
    private async Task Clear()
    {
        await store.Delete();
        Session = null;
        _twoFactorChallenge = null;
        Publish(AuthenticationStatus.LoggedOut);
    }
}
