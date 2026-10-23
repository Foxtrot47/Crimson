using System.Net;
using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Crimson.Tests;

public sealed class PlaytimeRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-playtime-{Guid.NewGuid():N}");
    private readonly CapturedLogs _logs = new();
    private readonly Logger _logger;
    private readonly HttpClient _authClient;
    private readonly HttpClient _apiClient;
    private readonly StubHandler _handler = new();
    private readonly Storage _storage;
    private readonly AuthManager _auth;
    private readonly EpicGamesRepository _repository;

    public PlaytimeRepositoryTests()
    {
        _logger = new LoggerConfiguration().WriteTo.Sink(_logs).CreateLogger();
        _storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        _authClient = new HttpClient(new StubHandler());
        _apiClient = new HttpClient(_handler);
        _auth = new AuthManager(_logger, _storage, new TestCredentialProtector(), _authClient);
        _repository = new EpicGamesRepository(_auth, _logger, _apiClient, _apiClient, _apiClient);
    }

    [Theory]
    [InlineData("6540", 6540L)]
    [InlineData("0", 0L)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("\"6540\"", null)]
    [InlineData("null", null)]
    [InlineData("9223372036854775808", null)]
    public void ParserAcceptsOnlyNonnegativeIntegerSeconds(string totalTime, long? expected)
    {
        var json = $$"""{"accountId":"test-account","artifactId":"game","totalTime":{{totalTime}}}""";
        Assert.Equal(expected, EpicGamesRepository.ParsePlaytimeSeconds(json, "game", "test-account"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"accountId\":\"other-account\",\"artifactId\":\"game\",\"totalTime\":42}")]
    [InlineData("{\"accountId\":\"test-account\",\"artifactId\":\"other-game\",\"totalTime\":42}")]
    public void ParserRejectsMissingOrMismatchedIdentity(string json)
    {
        Assert.Null(EpicGamesRepository.ParsePlaytimeSeconds(json, "game", "test-account"));
    }

    [Fact]
    public async Task FetchUsesAuthenticatedArtifactEndpoint()
    {
        await SignIn();
        _handler.Respond = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/test-account/artifact/game", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-access", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"accountId":"test-account","artifactId":"game","totalTime":6540}"""));
        };

        Assert.Equal(6540L, await _repository.FetchGamePlaytimeSeconds("game"));
        Assert.Equal(1, _handler.Requests);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task UnavailableServiceDoesNotFabricateZero(int status)
    {
        await SignIn();
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));

        Assert.Null(await _repository.FetchGamePlaytimeSeconds("game"));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        await SignIn();
        using var cancellation = new CancellationTokenSource();
        _handler.Respond = (_, token) => Task.FromCanceled<HttpResponseMessage>(token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.FetchGamePlaytimeSeconds("game", cancellation.Token));
    }

    [Fact]
    public async Task ErrorsDoNotLogRequestPathsOrCredentials()
    {
        await SignIn();
        _handler.Respond = (_, _) => throw new HttpRequestException("test-account synthetic-access private-request-path");

        Assert.Null(await _repository.FetchGamePlaytimeSeconds("game"));
        var logs = string.Join('\n', _logs.Messages);
        Assert.DoesNotContain("test-account", logs);
        Assert.DoesNotContain("synthetic-access", logs);
        Assert.DoesNotContain("private-request-path", logs);
    }

    [Fact]
    public async Task LoggedOutAccountMakesNoPlaytimeRequest()
    {
        Assert.Null(await _repository.FetchGamePlaytimeSeconds("game"));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task MalformedResponseRemainsUnavailable()
    {
        await SignIn();
        _handler.Respond = (_, _) => Task.FromResult(Json("not-json"));
        Assert.Null(await _repository.FetchGamePlaytimeSeconds("game"));
    }

    private async Task SignIn()
    {
        await _storage.SaveUserData(new UserData
        {
            AccountId = "test-account",
            AccessToken = "synthetic-access",
            RefreshToken = "synthetic-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O")
        });
        Assert.Equal(AuthenticationStatus.LoggedIn, await _auth.CheckAuthStatus());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        _authClient.Dispose();
        _apiClient.Dispose();
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CapturedLogs : ILogEventSink
    {
        public List<string> Messages { get; } = [];
        public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage() + logEvent.Exception);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Respond(request, cancellationToken);
        }
    }
}
