using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Providers.Ubisoft;

namespace Crimson.Core.Tests.Ubisoft;

public class UbisoftAuthenticationTests
{
    internal static object SessionPayload(string? remember = "refresh") => new
    {
        ticket = "login-secret", sessionId = "session", profileId = "profile", userId = "user",
        nameOnPlatform = "Player", expiration = DateTimeOffset.UtcNow.AddHours(3), rememberMeTicket = remember
    };
    [Fact]
    public async Task PasswordAndTwoFactorAreSeparateAndOnlyAdoptAfterSuccess()
    {
        var requests = new List<string>();
        var api = new UbisoftApi(new HttpClient(new Handler(async r =>
        {
            requests.Add(r.Headers.Authorization!.ToString());
            var body = await r.Content!.ReadAsStringAsync();
            if (requests.Count == 1)
            {
                Assert.Contains("rememberMe", body);
                return Json(new { twoFactorAuthenticationTicket = "2fa-secret" });
            }
            Assert.Equal("123456", r.Headers.GetValues("Ubi-2FA-Code").Single());
            return Json(SessionPayload());
        })));
        var store = new MemoryStore();
        var auth = new UbisoftAuthentication(api, store);
        Assert.True(await auth.SignIn("üser@example.com", "päss", true));
        Assert.Equal(AuthenticationStatus.LoggedOut, auth.AuthenticationStatus);
        Assert.Null(store.Session);
        await auth.CompleteTwoFactor("123456");
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("üser@example.com:päss")), requests[0]);
        Assert.Equal("ubi_2fa_v1 t=2fa-secret", requests[1]);
        Assert.Equal(AuthenticationStatus.LoggedIn, auth.AuthenticationStatus);
        Assert.Equal("login-secret", store.Session!.Ticket);
    }
    [Fact]
    public async Task NonRememberedLoginDeletesOldAccountAndLogoutClearsMemory()
    {
        var store = new MemoryStore();
        var api = new UbisoftApi(new HttpClient(new Handler(_ => Task.FromResult(Json(SessionPayload())))));
        var auth = new UbisoftAuthentication(api, store);
        await auth.SignIn("email", "password", false);
        Assert.Null(store.Session);
        Assert.Null(auth.Session!.RememberMeTicket);
        await auth.Logout();
        Assert.Null(auth.Session);
        Assert.Equal(AuthenticationStatus.LoggedOut, auth.AuthenticationStatus);
    }
    [Fact]
    public async Task ExpiredSavedSessionRefreshesBeforeValidationAndRejectionClearsIt()
    {
        var store = new MemoryStore { Session = new UbisoftSession
        {
            Ticket = "expired", SessionId = "old", ProfileId = "profile", UserId = "user", ExpiresAt = DateTimeOffset.MinValue,
            RememberMeTicket = "refresh"
        }};
        var count = 0;
        var api = new UbisoftApi(new HttpClient(new Handler(r =>
        {
            count++;
            Assert.Equal(count == 1 ? "ubi_v1 t=refresh" : "Ubi_v1 t=login-secret", r.Headers.Authorization!.ToString());
            return Task.FromResult(count == 1 ? Json(SessionPayload()) : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        })));
        var auth = new UbisoftAuthentication(api, store);
        Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
        Assert.Null(auth.Session);
        Assert.Null(store.Session);
        Assert.Equal(2, count);
    }
    [Fact]
    public async Task ProtectedStoreDoesNotWritePlainTokensOrPasswords()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = new UbisoftSessionStore(root, new TestProtector());
            var session = UbisoftSession.Parse(JsonSerializer.SerializeToElement(SessionPayload()), true);
            await store.Save(session, default);
            var file = await File.ReadAllTextAsync(Path.Combine(root, "session.dat"));
            Assert.DoesNotContain("login-secret", file);
            Assert.DoesNotContain("refresh", file);
            Assert.Equal(session.Ticket, (await store.Load(default))!.Ticket);
            Assert.DoesNotContain(session.Ticket, session.ToString());
            await store.Delete();
            Assert.Null(await store.Load(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request);
    }
    private sealed class TestProtector : ICredentialProtector
    {
        public string Protect(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
    internal sealed class MemoryStore : IUbisoftSessionStore
    {
        public UbisoftSession? Session;
        public Task<UbisoftSession?> Load(CancellationToken token) => Task.FromResult(Session);
        public Task Save(UbisoftSession session, CancellationToken token) { Session = session; return Task.CompletedTask; }
        public Task Delete() { Session = null; return Task.CompletedTask; }
    }
}
