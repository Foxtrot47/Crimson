using System.Net;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class AuthFailurePreservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crimson-auth-failure-" + Guid.NewGuid().ToString("N"));
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData(false, "transport")]
    [InlineData(true, "transport")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    [InlineData(false, "server")]
    [InlineData(true, "server")]
    [InlineData(false, "rate-limit")]
    [InlineData(true, "rate-limit")]
    public async Task StartupTransientFailuresPreserveTheSavedSessionAndCachesWithoutAuthorizing(bool expired, string failure)
    {
        var storage = await Seed(expired);
        var before = Snapshot();
        using var client = new HttpClient(new Handler(_ => Failure(failure)));
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), client);

        Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
        Assert.Null(await auth.GetAccessToken());
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("unrelated-400")]
    [InlineData("malformed-error")]
    [InlineData("malformed-success")]
    [InlineData("missing-token")]
    public async Task UnrecognizedRefreshResponsesDoNotDeleteSavedData(string failure)
    {
        var storage = await Seed(expired: true);
        var before = Snapshot();
        using var client = new HttpClient(new Handler(_ => failure switch
        {
            "unrelated-400" => Response(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}"),
            "malformed-error" => Response(HttpStatusCode.BadRequest, "not-json"),
            "malformed-success" => Response(HttpStatusCode.OK, "not-json"),
            _ => Response(HttpStatusCode.OK, "{}")
        }));
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), client);

        Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitCredentialRejectionStillClearsTheSession(bool expired, bool invalidGrant)
    {
        var storage = await Seed(expired);
        using var client = new HttpClient(new Handler(_ => invalidGrant
            ? Response(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")
            : Response(HttpStatusCode.Unauthorized, "{}")));
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), client);

        Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
        Assert.False(File.Exists(Path.Combine(_root, "user.json")));
        Assert.False(File.Exists(Path.Combine(_root, "assets.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "metadata")));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("server")]
    public async Task MidSessionRefreshCanRecoverAfterATransientFailureWithoutReturningAnExpiredToken(string failure)
    {
        var storage = await Seed(expired: false);
        var unavailable = true;
        using var client = new HttpClient(new Handler(request => request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, "{}")
            : unavailable ? Failure(failure) : Response(HttpStatusCode.OK, JsonSerializer.Serialize(User(expired: false)))));
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), client);
        Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
        await storage.SaveUserData(User(expired: true));
        var before = Snapshot();

        Assert.Null(await auth.GetAccessToken());
        AssertSnapshot(before);
        unavailable = false;
        Assert.NotNull(await auth.GetAccessToken());
        Assert.True(File.Exists(Path.Combine(_root, "user.json")));
    }

    private async Task<Storage> Seed(bool expired)
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        await storage.SaveUserData(User(expired));
        await storage.SaveGameAssetsData([]);
        await File.WriteAllTextAsync(Path.Combine(_root, "metadata", "synthetic.json"), "{}");
        return storage;
    }

    private static UserData User(bool expired) => new()
    {
        AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToString("O"),
        RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O")
    };

    private Dictionary<string, byte[]> Snapshot() => new[] { "user.json", "assets.json", Path.Combine("metadata", "synthetic.json") }
        .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(_root, name)));

    private void AssertSnapshot(Dictionary<string, byte[]> expected)
    {
        foreach (var (name, bytes) in expected)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, name)));
    }

    private static HttpResponseMessage Failure(string failure) => failure switch
    {
        "transport" => throw new HttpRequestException("Synthetic network failure"),
        "timeout" => throw new TaskCanceledException("Synthetic timeout"),
        "rate-limit" => Response(HttpStatusCode.TooManyRequests, "{}"),
        _ => Response(HttpStatusCode.ServiceUnavailable, "{}")
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
