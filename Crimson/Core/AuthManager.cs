using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Repository;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public class AuthManager
{
    private readonly ILogger _log;
    private readonly Storage _storage;

    private AuthenticationStatus _authenticationStatus;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private const string BasicAuthUsername = "34a02cf8f4414e29b15921876da36f9a";
    private const string BasicAuthPassword = "daafbccc737745039dffe53d94fc76cf";
    private const string OAuthHost = "https://account-public-service-prod03.ol.epicgames.com";
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;


    public delegate void AuthStatusChangedEventHandler(object sender, AuthStatusChangedEventArgs e);

    public event AuthStatusChangedEventHandler AuthStatusChanged;

    public AuthenticationStatus AuthenticationStatus => _authenticationStatus;


    public AuthManager(ILogger log, Storage storage, HttpClient httpClient)
    {
        _log = log;
        _storage = storage;
        _httpClient = httpClient;
    }

    // <summary>
    // Check if the user is logged in or not
    // </summary>
    public async Task<AuthenticationStatus> CheckAuthStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            _authenticationStatus = AuthenticationStatus.Checking;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));

            var userData = await _storage.GetUserData();
            if (userData == null)
            {
                _authenticationStatus = AuthenticationStatus.LoggedOut;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
                return _authenticationStatus;
            }

            if (userData.AccessToken == null)
            {
                _log.Error("CheckAuthStatus: Failed to parse user data from string");
                throw new Exception("CheckAuthStatus: Failed to parse user data");
            }

            userData.AccessToken = KeyManager.DecryptString(userData.AccessToken);
            userData.RefreshToken = KeyManager.DecryptString(userData.RefreshToken);

            // check if the refresh token expiry date is in the past and if it is then log the user out
            var refreshExpiryDate = DateTimeOffset.Parse(userData.RefreshExpiresAt);
            if (refreshExpiryDate < DateTimeOffset.UtcNow)
            {
                _log.Information("CheckAuthStatus: Refresh token expired, logging out");
                _authenticationStatus = AuthenticationStatus.LoggedOut;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
                return _authenticationStatus;
            }

            // check if the access token expiry date is in the past (with buffer) and if it is then refresh
            var expiryDate = DateTimeOffset.Parse(userData.ExpiresAt);
            if (expiryDate < DateTimeOffset.UtcNow + TokenRefreshBuffer)
            {
                _log.Information("CheckAuthStatus: Access token expired or expiring soon, refreshing");
                var newData = await RequestTokens(
                    "refresh_token",
                    "refresh_token",
                    userData.RefreshToken,
                    cancellationToken);
                if (newData == null || newData.AccessToken == null)
                {
                    _log.Error("CheckAuthStatus: Token refresh failed, logging out");
                    _authenticationStatus = AuthenticationStatus.LoggedOut;
                    OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
                    return _authenticationStatus;
                }

                // Keep plain access token for verification
                var plainAccessToken = newData.AccessToken;
                newData.AccessToken = KeyManager.EncryptString(newData.AccessToken);
                newData.RefreshToken = KeyManager.EncryptString(newData.RefreshToken);
                await _storage.SaveUserData(newData);

                if (!await VerifyAccessToken(plainAccessToken, cancellationToken))
                {
                    _log.Warning("CheckAuthStatus: Refreshed access token is invalid, logging out");
                    _authenticationStatus = AuthenticationStatus.LoggedOut;
                    OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
                    return _authenticationStatus;
                }
            }
            else
            {
                _log.Information("CheckAuthStatus: Access token is still valid");

                if (!await VerifyAccessToken(userData.AccessToken, cancellationToken))
                {
                    _log.Warning("CheckAuthStatus: Access token is invalid, logging out");
                    _authenticationStatus = AuthenticationStatus.LoggedOut;
                    OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
                    return _authenticationStatus;
                }
            }

            _authenticationStatus = AuthenticationStatus.LoggedIn;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
            return _authenticationStatus;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("CheckAuthStatus failed with {ErrorType}", ex.GetType().Name);
            _authenticationStatus = AuthenticationStatus.LoggedOut;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
            return _authenticationStatus;
        }
    }

    /// <summary>
    /// Fetch user data from the exchange code
    /// </summary>
    public async Task DoExchangeLogin(
        string exchangeCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userData = await RequestTokens(
                "exchange_code",
                "exchange_code",
                exchangeCode,
                cancellationToken);

            if (userData == null || userData.AccessToken == null)
            {
                _log.Error("DoExchangeLogin: Failed to fetch tokens");
                _authenticationStatus = AuthenticationStatus.LoginFailed;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
                return;
            }

            userData.AccessToken = KeyManager.EncryptString(userData.AccessToken);
            userData.RefreshToken = KeyManager.EncryptString(userData.RefreshToken);
            _log.Information("RequestTokens: Tokens successfully encrypted");

            await _storage.SaveUserData(userData);

            _authenticationStatus = AuthenticationStatus.LoggedIn;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedIn));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("DoExchangeLogin failed with {ErrorType}", ex.GetType().Name);
            _authenticationStatus = AuthenticationStatus.LoginFailed;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
        }
    }

    public async Task<string> GetAccessToken(CancellationToken cancellationToken = default)
    {
        if (_authenticationStatus != AuthenticationStatus.LoggedIn)
        {
            _log.Error("GetAccessToken: User is not logged in");
            return null;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var userData = await _storage.GetUserData();
            if (userData == null) return null;

            var plainAccessToken = KeyManager.DecryptString(userData.AccessToken);
            var plainRefreshToken = KeyManager.DecryptString(userData.RefreshToken);

            var expiryDate = DateTimeOffset.Parse(userData.ExpiresAt);
            if (expiryDate < DateTimeOffset.UtcNow + TokenRefreshBuffer)
            {
                _log.Information("GetAccessToken: Token expired or expiring soon, refreshing");

                var refreshExpiryDate = DateTimeOffset.Parse(userData.RefreshExpiresAt);
                if (refreshExpiryDate < DateTimeOffset.UtcNow)
                {
                    _log.Error("GetAccessToken: Refresh token also expired, logging out");
                    _authenticationStatus = AuthenticationStatus.LoggedOut;
                    OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
                    return null;
                }

                var newData = await RequestTokens(
                    "refresh_token",
                    "refresh_token",
                    plainRefreshToken,
                    cancellationToken);
                if (newData == null || newData.AccessToken == null)
                {
                    _log.Error("GetAccessToken: Token refresh failed, logging out");
                    _authenticationStatus = AuthenticationStatus.LoggedOut;
                    OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
                    return null;
                }

                plainAccessToken = newData.AccessToken;
                newData.AccessToken = KeyManager.EncryptString(newData.AccessToken);
                newData.RefreshToken = KeyManager.EncryptString(newData.RefreshToken);
                await _storage.SaveUserData(newData);
            }

            return plainAccessToken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("GetAccessToken failed with {ErrorType}", ex.GetType().Name);
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<UserData> GetUserData()
    {
        if (_authenticationStatus != AuthenticationStatus.LoggedIn)
        {
            _log.Error("GetUserData: User is not logged in");
            return null;
        }
        return await _storage.GetUserData();
    }

    public async Task Logout()
    {
        _log.Information("Logout: Logging out");
        _authenticationStatus = AuthenticationStatus.LoggedOut;
        await _storage.ClearUserData();
        OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
    }

    private async Task<UserData> RequestTokens(
        string grantType,
        string codeName,
        string codeValue,
        CancellationToken cancellationToken)
    {
        const long maximumAuthenticationResponseBytes = 1024 * 1024;
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{BasicAuthUsername}:{BasicAuthPassword}"));
        using var formData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>(codeName, codeValue),
            new KeyValuePair<string, string>("grant_type", grantType),
            new KeyValuePair<string, string>("token_type", "eg1")
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            EpicEndpointPolicy.RequireApiUri($"{OAuthHost}/account/api/oauth/token"))
        {
            Content = formData
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.Error(
                    "RequestTokens failed with HTTP {StatusCode} {ReasonPhrase}",
                    (int)response.StatusCode,
                    response.ReasonPhrase);
                return null;
            }

            var bytes = await BoundedHttpContent.ReadBytesAsync(
                response.Content,
                maximumAuthenticationResponseBytes,
                cancellationToken);
            var userData = JsonSerializer.Deserialize<UserData>(bytes);
            if (userData?.AccessToken == null)
            {
                _log.Error("RequestTokens returned an invalid authentication response");
                return null;
            }

            return userData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("RequestTokens failed with {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    // <summary>
    // Verify the access token is still valid
    // </summary>
    private async Task<bool> VerifyAccessToken(
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
        catch (Exception ex)
        {
            _log.Error("VerifyAccessToken failed with {ErrorType}", ex.GetType().Name);
            return false;
        }
    }


    // Wrap event invocations inside a protected virtual method
    // to allow derived classes to override the event invocation behavior.
    // Wrap event invocations inside a private static method.
    private void OnAuthStatusChanged(AuthStatusChangedEventArgs e)
    {
        AuthStatusChanged?.Invoke(null, e);
    }
}

public enum AuthenticationStatus
{
    Checking,
    LoggedOut,
    LoggedIn,
    LoginFailed
}

public class AuthStatusChangedEventArgs(AuthenticationStatus newStatus) : EventArgs
{
    public AuthenticationStatus NewStatus { get; } = newStatus;
}

public class EpicLoginResponse
{
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; }
}
