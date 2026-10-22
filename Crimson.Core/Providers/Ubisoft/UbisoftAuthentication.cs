using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;

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

public sealed class UbisoftAuthentication(UbisoftApi api, IUbisoftSessionStore store) : ILauncherAuthentication
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _twoFactorChallenge;
    private bool _remember;
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
            await Adopt(UbisoftSession.Parse(json, remember), remember, token);
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
            await Adopt(UbisoftSession.Parse(json, _remember), _remember, token);
            _twoFactorChallenge = null;
        }
        finally { _gate.Release(); }
    }
    public void CancelSignIn() => _twoFactorChallenge = null;
    public async Task<AuthenticationStatus> CheckAuthStatus()
    {
        await _gate.WaitAsync();
        try
        {
            Publish(AuthenticationStatus.Checking);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var session = Session ?? await store.Load(timeout.Token);
            if (session is null) { Publish(AuthenticationStatus.LoggedOut); return AuthenticationStatus; }
            if (session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                if (string.IsNullOrEmpty(session.RememberMeTicket)) { await Clear(); return AuthenticationStatus; }
                session = UbisoftSession.Parse(await api.Remember(session.RememberMeTicket, timeout.Token), true);
            }
            await api.Get(session, "/v3/users/me", timeout.Token);
            await Adopt(session, !string.IsNullOrEmpty(session.RememberMeTicket), timeout.Token);
        }
        catch (UbisoftApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { await Clear(); }
        catch (Exception) { Session = null; Publish(AuthenticationStatus.LoginFailed); }
        finally { _gate.Release(); }
        return AuthenticationStatus;
    }
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
        if (remember) await store.Save(session, token); else await store.Delete();
        Session = session;
        Publish(AuthenticationStatus.LoggedIn);
    }
    public async Task Logout()
    {
        await _gate.WaitAsync();
        try { await Clear(); }
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
