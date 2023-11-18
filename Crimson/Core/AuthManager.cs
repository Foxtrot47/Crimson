using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Storage;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public static class AuthManager
{
    private static ILogger _log;

    private static string _userDataFile;
    private static AuthenticationStatus _authenticationStatus;

    private static readonly string BasicAuthUsername = "34a02cf8f4414e29b15921876da36f9a";
    private static readonly string BasicAuthPassword = "daafbccc737745039dffe53d94fc76cf";
    private static readonly string OAuthHost = "https://account-public-service-prod03.ol.epicgames.com";
    private static readonly string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    private static readonly HttpClient HttpClient;


    public delegate void AuthStatusChangedEventHandler(object sender, AuthStatusChangedEventArgs e);

    public static event AuthStatusChangedEventHandler AuthStatusChanged;

    public static AuthenticationStatus AuthenticationStatus => _authenticationStatus;

    static AuthManager()
    {
        HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public static void Initialize(ILogger log)
    {
        _log = log;
        var localFolder = ApplicationData.Current.LocalFolder;
        _userDataFile = $@"{localFolder.Path}\user.json";
    }
    // <summary>
    // Check if the user is logged in or not
    // </summary>
    public static async Task<AuthenticationStatus> CheckAuthStatus()
    {
        try
        {
            _authenticationStatus = AuthenticationStatus.Checking;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));

            if (!File.Exists(_userDataFile))
            {
                _authenticationStatus = AuthenticationStatus.LoggedOut;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));

                // Create the file on exit
                await SaveAuthData(new UserData());
                return _authenticationStatus;
            }

            UserData userData = null;

            await using (var fileStream = File.Open(_userDataFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var streamReader = new StreamReader(fileStream))
            {
                var jsonString = await streamReader.ReadToEndAsync();
                userData = JsonSerializer.Deserialize<UserData>(jsonString);
            }

            if (userData.AccessToken == null)
            {
                _log.Error("CheckAuthStatus: Failed to parse user data from string");
                throw new Exception("CheckAuthStatus: Failed to parse user data");
            }

            userData.AccessToken = KeyManager.DecryptString(userData.AccessToken);
            userData.RefreshToken = KeyManager.DecryptString(userData.RefreshToken);

            // check if the refresh token expiry date is in the past and if it is then log the user out
            var refreshExpiryDate = DateTime.Parse(userData.RefreshExpiresAt);
            if (refreshExpiryDate < DateTime.Now)
            {
                _log.Information("CheckAuthStatus: Refresh token expired, logging out");
                _authenticationStatus = AuthenticationStatus.LoggedOut;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
                return _authenticationStatus;
            }

            // check if the access token expiry date is in the past and if it is then refresh the token calling another method
            var expiryDate = DateTime.Parse(userData.ExpiresAt);
            if (expiryDate < DateTime.Now)
            {
                _log.Information("CheckAuthStatus: Access token expired, refreshing");
                var newData= await RequestTokens("refresh_token", "refresh_token", userData.RefreshToken);
                userData = newData;
                newData.AccessToken = KeyManager.EncryptString(newData.AccessToken);
                newData.RefreshToken = KeyManager.EncryptString(newData.RefreshToken);
                await SaveAuthData(userData);
            }
            else
            {
                _log.Information("CheckAuthStatus: Access token is still valid");
            }

            if (!await VerifyAccessToken(userData.AccessToken))
            {
                _log.Warning("CheckAuthStatus: Access token is invalid, logging out");
                _authenticationStatus = AuthenticationStatus.LoggedOut;
                OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
                return _authenticationStatus;
            }

            _authenticationStatus = AuthenticationStatus.LoggedIn;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
            return _authenticationStatus;
        }
        catch (Exception ex)
        {
            _log.Error($"CheckAuthStatus: {ex}");
            _authenticationStatus = AuthenticationStatus.LoggedOut;
            OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
            return _authenticationStatus;
        }
    }

    // <summary>
    // Fetch user data from the exchange code
    // </summary>
    public static async void DoExchangeLogin(string exchangeCode)
    {
        var userData = await RequestTokens("exchange_code", "exchange_code", exchangeCode);

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

        // Announce that we are authenticated
        _authenticationStatus = AuthenticationStatus.LoggedIn;
        OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedIn));

        await SaveAuthData(userData);
    }

    private static async Task<UserData> RequestTokens(string grantType, string codeName, string codeValue)
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{BasicAuthUsername}:{BasicAuthPassword}"));

        var formData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>(codeName, codeValue),
            new KeyValuePair<string, string>("grant_type", grantType),
            new KeyValuePair<string, string>("token_type", "eg1")
        });

        // Set the Authorization header with the Basic authentication credentials
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        try
        {
            // Make the API call with the form data
            var httpResponse = await HttpClient.PostAsync($"{OAuthHost}/account/api/oauth/token", formData);

            // Check if the request was successful (status code 200)
            if (httpResponse.IsSuccessStatusCode)
            {
                // Parse and use the response content here
                var result = await httpResponse.Content.ReadAsStringAsync();
                var userData = JsonSerializer.Deserialize<UserData>(result);

                if (userData.AccessToken == null)
                {
                    _log.Error("RequestTokens: Failed to parse user data from string");
                    throw new Exception("RequestTokens: Failed to parse user data");
                }
                return userData;
            }
            else
            {
                var result = await httpResponse.Content.ReadAsStringAsync();
                _log.Error($"RequestTokens: Failed to fetch tokens: {httpResponse.ReasonPhrase} - {result}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"RequestTokens: {ex.Message}");
            return null;
        }
    }

    private static async Task SaveAuthData(UserData data)
    {
        var jsonString = JsonSerializer.Serialize(data);

        await using var fileStream = File.Open(_userDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var streamWriter = new StreamWriter(fileStream);
        await streamWriter.WriteAsync(jsonString);
        await streamWriter.FlushAsync();
        _log.Information("SaveAuthData: successfully saved userdata to disk");
    }

    // <summary>
    // Verify the access token is still valid
    // </summary>
    private static async Task<bool> VerifyAccessToken(string accessToken)
    {
        try
        {
            // add bearer token to httpcleint
            HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Make the API call with the form data
            var htpResponse = await HttpClient.GetAsync($"{OAuthHost}/account/api/oauth/verify");

            // Check if the request was successful (status code 200)
            if (htpResponse.IsSuccessStatusCode)
                return true;
            else
                return false;
        }
        catch (Exception ex)
        {
            _log.Error($"VerifyAccessToken: {ex.Message}");
            return false;
        }
    }


    // Wrap event invocations inside a protected virtual method
    // to allow derived classes to override the event invocation behavior.
    // Wrap event invocations inside a private static method.
    private static void OnAuthStatusChanged(AuthStatusChangedEventArgs e)
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