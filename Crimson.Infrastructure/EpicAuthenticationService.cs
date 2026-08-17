using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Microsoft.Extensions.Logging;

namespace Crimson.Infrastructure;

public sealed class EpicAuthenticationService : IEpicAuthenticationService
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string OAuthHost = "https://account-public-service-prod03.ol.epicgames.com";
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EpicAuthenticationService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public EpicAuthenticationService(
        ICredentialStore credentials,
        HttpClient httpClient,
        ILogger<EpicAuthenticationService> logger)
    {
        _credentials = credentials;
        _httpClient = httpClient;
        _logger = logger;
    }

    public EpicAuthenticationSnapshot Snapshot { get; private set; } =
        new(EpicAuthenticationState.LoggedOut);

    public event EventHandler<EpicAuthenticationSnapshot>? Changed;

    public async Task<EpicAuthenticationSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.Checking));
        var user = await _credentials.GetUserData();
        if (user is null)
            return Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedOut));

        var token = await GetAccessToken(cancellationToken);
        if (string.IsNullOrWhiteSpace(token) ||
            !await VerifyAccessTokenAsync(token, cancellationToken))
        {
            await _credentials.ClearUserData();
            return Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedOut));
        }

        return Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedIn, user.DisplayName));
    }

    public Task<EpicAuthenticationSnapshot> LoginWithExchangeCodeAsync(
        string exchangeCode,
        CancellationToken cancellationToken = default) =>
        LoginWithCodeAsync(
            exchangeCode,
            "exchange_code",
            "exchange_code",
            "exchange code",
            cancellationToken);

    private async Task<EpicAuthenticationSnapshot> LoginWithCodeAsync(
        string code,
        string grantType,
        string codeName,
        string codeDescription,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > EpicLoginMessageGate.MaximumCodeLength)
            return Publish(new EpicAuthenticationSnapshot(
                EpicAuthenticationState.Failed,
                Error: $"Epic returned an invalid {codeDescription}."));

        Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.Authenticating));
        var user = await RequestTokensAsync(grantType, codeName, code, cancellationToken);
        if (user is null)
            return Publish(new EpicAuthenticationSnapshot(
                EpicAuthenticationState.Failed,
                Error: "Epic authentication failed."));

        await _credentials.SaveUserData(user);
        return Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedIn, user.DisplayName));
    }



    public async Task<string?> GetAccessToken(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _credentials.GetUserData();
            if (user is null || string.IsNullOrWhiteSpace(user.AccessToken))
                return null;
            if (!DateTimeOffset.TryParse(user.ExpiresAt, out var expiresAt))
            {
                await _credentials.ClearUserData();
                return null;
            }
            if (expiresAt >= DateTimeOffset.UtcNow + RefreshBuffer)
                return user.AccessToken;
            if (string.IsNullOrWhiteSpace(user.RefreshToken) ||
                !DateTimeOffset.TryParse(user.RefreshExpiresAt, out var refreshExpiresAt) ||
                refreshExpiresAt <= DateTimeOffset.UtcNow)
            {
                await _credentials.ClearUserData();
                return null;
            }

            var refreshed = await RequestTokensAsync(
                "refresh_token",
                "refresh_token",
                user.RefreshToken,
                cancellationToken);
            if (refreshed is null)
            {
                await _credentials.ClearUserData();
                return null;
            }

            await _credentials.SaveUserData(refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _credentials.ClearUserData();
        Publish(new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedOut));
    }

    private async Task<bool> VerifyAccessTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            EpicEndpointPolicy.RequireApiUri($"{OAuthHost}/account/api/oauth/verify"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Epic token verification failed with {ErrorType}",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<UserData?> RequestTokensAsync(
        string grantType,
        string codeName,
        string codeValue,
        CancellationToken cancellationToken)
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>(codeName, codeValue),
            new KeyValuePair<string, string>("grant_type", grantType),
            new KeyValuePair<string, string>("token_type", "eg1")
        ]);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            EpicEndpointPolicy.RequireApiUri($"{OAuthHost}/account/api/oauth/token"))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Epic token request failed with HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var bytes = await BoundedHttpContent.ReadBytesAsync(response.Content, 1024 * 1024, cancellationToken);
            return JsonSerializer.Deserialize<UserData>(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Epic token request failed with {ErrorType}", exception.GetType().Name);
            return null;
        }
    }

    private EpicAuthenticationSnapshot Publish(EpicAuthenticationSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
        return snapshot;
    }
}
