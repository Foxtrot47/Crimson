using System.Net;
using System.Text.Json;
using Crimson.Core;
using Crimson.Providers.Ubisoft;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;

namespace Crimson.Core.Tests.Ubisoft;

/// <summary>
/// Staying signed in across launches: Ubisoft's ticket dies after hours and its remember-me
/// renewal rejects third-party clients, so the remembered sign-in is replayed instead.
/// </summary>
public class UbisoftSavedSessionTests
{
    private const string Email = "player@example.test";
    private const string Password = "correct-horse-4b1";

    [Fact]
    public async Task ValidTicketIsExtendedInsteadOfWaitingToExpire()
    {
        using var fixture = new Fixture();
        fixture.Store.Session = LiveSession(TimeSpan.FromMinutes(10));
        fixture.Respond = request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Ok(ExtendedPayload(TimeSpan.FromHours(3)))
            : Ok(new { userId = "user" });

        var auth = fixture.Auth();

        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
        // The stored ticket now carries the extended expiry.
        Assert.NotNull(fixture.Store.Session);
        Assert.True(fixture.Store.Session!.ExpiresAt > DateTimeOffset.UtcNow.AddHours(2));
        Assert.Equal(Email, fixture.Store.Session.Email);
    }

    [Fact]
    public async Task LaunchExtendsTheStoredTicketAndAdoptsTheResult()
    {
        using var fixture = new Fixture();
        fixture.Store.Session = LiveSession(TimeSpan.FromHours(2));
        var calls = new List<string>();
        fixture.Respond = request =>
        {
            var auth = request.Headers.Authorization?.ToString() ?? "";
            calls.Add(request.RequestUri!.AbsolutePath + " " + auth);
            return request.RequestUri.AbsolutePath.Contains("sessions")
                ? Ok(ExtendedPayload(TimeSpan.FromHours(3)))
                : Ok(new { userId = "user" });
        };

        var auth = fixture.Auth();
        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());

        // One authenticated call is enough: the extension itself proves the ticket.
        Assert.Equal("/v3/profiles/sessions Ubi_v1 t=live-ticket", Assert.Single(calls));
        Assert.Equal("extended-ticket", auth.Session!.Ticket);
        Assert.True(fixture.Store.Session!.ExpiresAt > DateTimeOffset.UtcNow.AddHours(2));
    }

    [Fact]
    public async Task RenewalExtendsWhenTheSessionIsCloseToExpiry()
    {
        using var fixture = new Fixture();
        fixture.Store.Session = LiveSession(TimeSpan.FromMinutes(10));
        fixture.Respond = request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Ok(ExtendedPayload(TimeSpan.FromHours(3)))
            : Ok(new { userId = "user" });
        var auth = fixture.Auth();
        await auth.SignIn(Email, Password, remember: true);

        await auth.ExtendIfNeededAsync();

        Assert.NotNull(auth.Session);
        Assert.True(auth.Session!.ExpiresAt > DateTimeOffset.UtcNow.AddHours(2));
    }

    [Fact]
    public async Task RememberedSignInReconnectsAfterTheTicketDies()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Save(new UbisoftSignInCredential(Email, Password));
        fixture.Store.Session = ExpiredSession();
        fixture.Respond = request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Ok(SessionPayload())                                   // remember-me renewal also lands here
            : Ok(new { userId = "user" });

        var auth = fixture.Auth();

        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
        Assert.NotNull(auth.Session);
        Assert.Equal(Email, auth.Session!.Email);
        // The refreshed session is stored, so the next launch starts where this one ended.
        Assert.NotNull(fixture.Store.Session);
    }

    [Fact]
    public async Task ReconnectIsAttemptedEvenWhenNoSessionFileSurvives()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Save(new UbisoftSignInCredential(Email, Password));
        fixture.Respond = request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Ok(SessionPayload()) : Ok(new { userId = "user" });

        var auth = fixture.Auth();

        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
        Assert.NotNull(fixture.Store.Session);
    }

    [Fact]
    public async Task RejectedRememberedSignInLeavesTheFilesForANewAttempt()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Save(new UbisoftSignInCredential(Email, Password));
        fixture.Store.Session = ExpiredSession();
        fixture.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var auth = fixture.Auth();

        Assert.Equal(AuthenticationStatus.LoginFailed, await auth.CheckAuthStatus());
        Assert.Null(auth.Session);
        // Neither the account nor the remembered sign-in is thrown away by a rejection.
        Assert.NotNull(fixture.Store.Session);
        Assert.True(fixture.Credentials.Exists);
        Assert.Equal(Email, auth.LastEmail);
    }

    [Fact]
    public async Task OnlyTheRememberedSignInChoiceWritesCredentials()
    {
        using var fixture = new Fixture();
        fixture.Respond = request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Ok(SessionPayload()) : Ok(new { userId = "user" });

        var remembered = fixture.Auth();
        await remembered.SignIn(Email, Password, remember: true);
        Assert.True(fixture.Credentials.Exists);

        var once = fixture.Auth();
        await once.SignIn(Email, Password, remember: false);
        Assert.False(fixture.Credentials.Exists);
    }

    [Fact]
    public async Task DisconnectForgetsTheStoredSignIn()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Save(new UbisoftSignInCredential(Email, Password));
        fixture.Respond = _ => Ok(new { userId = "user" });

        var auth = fixture.Auth();
        await auth.Logout();

        Assert.False(fixture.Credentials.Exists);
        Assert.Equal("", auth.LastEmail);
    }

    [Fact]
    public void StoredSignInNeverHoldsThePasswordInPlaintext()
    {
        using var fixture = new Fixture();
        fixture.Credentials.Save(new UbisoftSignInCredential(Email, Password));

        var contents = File.ReadAllText(Path.Combine(fixture.Root, "signin.dat"));

        Assert.DoesNotContain(Password, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, contents, StringComparison.Ordinal);
        Assert.Equal(new UbisoftSignInCredential(Email, Password), fixture.Credentials.Load());
    }

    private static UbisoftSession LiveSession(TimeSpan lifetime) => new()
    {
        Ticket = "live-ticket", SessionId = "session", ProfileId = "profile", UserId = "user",
        Name = "Player", RememberMeTicket = "live-remember", Email = Email,
        ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime)
    };

    private static object ExtendedPayload(TimeSpan lifetime) => new
    {
        ticket = "extended-ticket", sessionId = "session", profileId = "profile", userId = "user",
        nameOnPlatform = "Player", rememberMeTicket = "extended-remember",
        expiration = DateTimeOffset.UtcNow.Add(lifetime)
    };

    private static UbisoftSession ExpiredSession() => new()
    {
        Ticket = "dead-ticket", SessionId = "session", ProfileId = "profile", UserId = "user",
        Name = "Player", RememberMeTicket = "dead-remember", Email = Email,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
    };

    private static HttpResponseMessage Ok(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload))
    };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-signin-" + Guid.NewGuid().ToString("N"));
        public MemoryStore Store { get; } = new();
        public UbisoftSignInCredentialStore Credentials { get; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Credentials = new UbisoftSignInCredentialStore(Root, new Protector());
        }

        public UbisoftAuthentication Auth() => new(
            new UbisoftApi(new HttpClient(new Handler(request => Task.FromResult(Respond(request))))),
            Store, Credentials);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (IOException) { }
        }

        private sealed class Protector : IUserCredentialProtector
        {
            public string Protect(string value) => "protected:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
            public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedValue["protected:".Length..]));
        }
    }
}
