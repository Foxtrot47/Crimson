using System.Net;
using System.Text.Json;
using Crimson.Providers.Ubisoft;
using Serilog;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;

namespace Crimson.Core.Tests.Ubisoft;

/// <summary>
/// Ubisoft's remember-me renewal answers 401 "Ticket is invalid" to third-party clients, so an
/// expired ticket must never cost the stored identity: the launcher needs the password again, not
/// the email and account name as well.
/// </summary>
public class UbisoftSessionRetentionTests
{
    [Fact]
    public async Task ExpiredTicketKeepsTheStoredSessionAndRemembersWhoItWas()
    {
        var store = new MemoryStore
        {
            Session = Stored("player@example.test", expired: true)
        };
        var api = Api(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var auth = new UbisoftAuthentication(api, store);

        var status = await auth.CheckAuthStatus();

        Assert.Equal(AuthenticationStatus.LoginFailed, status);
        Assert.Null(auth.Session);
        // The identity survives so the dialog can prefill it and only ask for the password.
        Assert.NotNull(store.Session);
        Assert.Equal("player@example.test", store.Session!.Email);
        Assert.Equal("player@example.test", auth.LastEmail);
    }

    [Fact]
    public async Task ValidationRejectionDoesNotForgetTheAccountEither()
    {
        var store = new MemoryStore { Session = Stored("player@example.test", expired: false) };
        var api = Api(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var auth = new UbisoftAuthentication(api, store);

        // A live ticket signs in at once; Ubisoft's refusal arrives with the background extension.
        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
        await auth.LaunchExtension;

        Assert.Equal(AuthenticationStatus.LoginFailed, auth.AuthenticationStatus);
        Assert.Null(auth.Session);
        Assert.NotNull(store.Session);
    }

    [Fact]
    public async Task SignedInSessionIsPersistedAndRefreshedInPlace()
    {
        var store = new MemoryStore();
        var api = Api(request => request.RequestUri!.AbsolutePath.Contains("sessions")
            ? Json(SessionPayload())
            : Json(new { userId = "user" }));
        var auth = new UbisoftAuthentication(api, store);

        await auth.SignIn("player@example.test", "password", remember: true);

        Assert.Equal(AuthenticationStatus.LoggedIn, auth.AuthenticationStatus);
        Assert.Equal("player@example.test", store.Session!.Email);
        Assert.Equal("player@example.test", auth.LastEmail);
    }

    [Fact]
    public async Task DisconnectForgetsEverything()
    {
        var store = new MemoryStore { Session = Stored("player@example.test", expired: false) };
        var auth = new UbisoftAuthentication(Api(_ => Json(new { userId = "user" })), store);
        await auth.CheckAuthStatus();
        await auth.LaunchExtension;
        Assert.NotNull(store.Session);

        await auth.Logout();

        Assert.Null(store.Session);
        Assert.Equal("", auth.LastEmail);
        Assert.Equal(AuthenticationStatus.LoggedOut, auth.AuthenticationStatus);
    }

    private static UbisoftSession Stored(string email, bool expired) => new()
    {
        Ticket = "login-secret", SessionId = "session", ProfileId = "profile", UserId = "user",
        Name = "Player", RememberMeTicket = "remember-secret", Email = email,
        ExpiresAt = expired
            ? DateTimeOffset.UtcNow.AddMinutes(-5)
            : DateTimeOffset.UtcNow.AddHours(2)
    };

    private static UbisoftApi Api(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(request => Task.FromResult(respond(request)))));

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload))
    };
}
