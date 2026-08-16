using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Crimson.Repository;

namespace Crimson.Tests;

public sealed class EpicGamesRepositoryTests
{
    [Fact]
    public async Task GetGameManifest_RetriesRetryableGetAndHonorsRetryAfter()
    {
        var requestCount = 0;
        using var contentClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return Task.FromResult(unavailable);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            });
        }));
        using var apiClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("API client should not be used.")));

        var repository = CreateRepository(apiClient, contentClient);

        var result = await repository.GetGameManifest(new GetManifestUrlData
        {
            BaseUrls = [],
            ManifestUrls = ["https://download.epicgames.com/test.manifest"],
            ManifestHash = string.Empty
        });

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 3, 4], result.Value);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task GetGameManifest_RejectsDeclaredOversizedBody()
    {
        using var contentClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 512L * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        using var apiClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("API client should not be used.")));

        var repository = CreateRepository(apiClient, contentClient);

        var result = await repository.GetGameManifest(new GetManifestUrlData
        {
            BaseUrls = [],
            ManifestUrls = ["https://download.epicgames.com/test.manifest"],
            ManifestHash = string.Empty
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(RepositoryFailureKind.SizeLimit, result.Failure?.Kind);
    }

    [Fact]
    public async Task GetGameManifest_PropagatesCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var contentClient = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var apiClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("API client should not be used.")));

        var repository = CreateRepository(apiClient, contentClient);
        using var cancellation = new CancellationTokenSource();

        var operation = repository.GetGameManifest(
            new GetManifestUrlData
            {
                BaseUrls = [],
                ManifestUrls = ["https://download.epicgames.com/test.manifest"],
                ManifestHash = string.Empty
            },
            cancellation.Token);
        await requestStarted.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task GetGameManifest_ReturnsTypedRedirectFailureWithoutFollowing()
    {
        var requestCount = 0;
        using var contentClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.test/manifest");
            return Task.FromResult(response);
        }));
        using var apiClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("API client should not be used.")));

        var repository = CreateRepository(apiClient, contentClient);

        var result = await repository.GetGameManifest(new GetManifestUrlData
        {
            BaseUrls = [],
            ManifestUrls = ["https://download.epicgames.com/test.manifest"],
            ManifestHash = string.Empty
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(RepositoryFailureKind.Http, result.Failure?.Kind);
        Assert.Equal(HttpStatusCode.Redirect, result.Failure?.StatusCode);
        Assert.Equal(1, requestCount);
    }

    private static EpicGamesRepository CreateRepository(
        HttpClient apiClient,
        HttpClient contentClient) => new(
            new StubAccessTokenProvider(),
            NullLogger<EpicGamesRepository>.Instance,
            apiClient,
            contentClient);

    private sealed class StubAccessTokenProvider : IAccessTokenProvider
    {
        public Task<string?> GetAccessToken(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("test-token");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
