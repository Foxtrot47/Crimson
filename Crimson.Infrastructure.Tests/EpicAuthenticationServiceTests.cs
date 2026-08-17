using System.Net;
using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Crimson.Infrastructure.Tests;

public sealed class EpicAuthenticationServiceTests
{
    [Fact]
    public async Task ExchangeCodeCreatesAuthenticatedSession()
    {
        string? requestBody = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token":"access",
                      "refresh_token":"refresh",
                      "expires_at":"2099-01-01T00:00:00Z",
                      "refresh_expires_at":"2099-02-01T00:00:00Z",
                      "displayName":"Player"
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var credentials = new InMemoryCredentialStore();
        var service = new EpicAuthenticationService(
            credentials,
            client,
            NullLogger<EpicAuthenticationService>.Instance);

        var result = await service.LoginWithExchangeCodeAsync("exchange-code");

        Assert.Equal(EpicAuthenticationState.LoggedIn, result.State);
        Assert.Equal("Player", result.DisplayName);
        Assert.Contains("grant_type=exchange_code", requestBody);
        Assert.Equal("access", await service.GetAccessToken());
        Assert.NotNull(await credentials.GetUserData());
    }

    [Fact]
    public async Task CheckVerifiesStoredAccessToken()
    {
        HttpRequestMessage? observedRequest = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            observedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveUserData(new UserData
        {
            AccessToken = "access",
            DisplayName = "Player",
            ExpiresAt = "2099-01-01T00:00:00Z"
        });
        var service = new EpicAuthenticationService(
            credentials,
            client,
            NullLogger<EpicAuthenticationService>.Instance);

        var result = await service.CheckAsync();

        Assert.Equal(EpicAuthenticationState.LoggedIn, result.State);
        Assert.Equal("/account/api/oauth/verify", observedRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", observedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("access", observedRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CheckRejectsStoredTokenWithMalformedExpiry()
    {
        var requestSent = false;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var credentials = new InMemoryCredentialStore();
        await credentials.SaveUserData(new UserData
        {
            AccessToken = "access",
            ExpiresAt = "not-a-date"
        });
        var service = new EpicAuthenticationService(
            credentials,
            client,
            NullLogger<EpicAuthenticationService>.Instance);

        var result = await service.CheckAsync();

        Assert.Equal(EpicAuthenticationState.LoggedOut, result.State);
        Assert.False(requestSent);
        Assert.Null(await credentials.GetUserData());
    }


    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
