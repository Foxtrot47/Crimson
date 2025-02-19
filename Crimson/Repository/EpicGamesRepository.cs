﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Repository
{
    internal class EpicGamesRepository : IStoreRepository
    {
        private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
        private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
        private const string OAuthHost = "account-public-service-prod03.ol.epicgames.com";
        private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

        private readonly HttpClient _httpClient;
        private readonly ILogger _log;
        private readonly AuthManager _authManager;

        public EpicGamesRepository(AuthManager authManager, ILogger logger, HttpClient httpClient)
        {
            _log = logger;
            _authManager = authManager;
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }

        public async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {

            _log.Information("FetchGameMetaData: Fetching game metadata");
            var accessToken = await _authManager.GetAccessToken();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // API requests parameters to be in query instead of body
            var qs = $"?id={catalogItemId}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en";

            try
            {
                // Make the API call with the form data
                var httpResponse = await _httpClient.GetAsync($"https://{CatalogHost}/catalog/api/shared/namespace/{nameSpace}/bulk/items{qs}");
                // Check if the request was successful (status code 200)
                if (httpResponse.IsSuccessStatusCode)
                {
                    // Parse and use the response content here
                    var result = await httpResponse.Content.ReadAsStringAsync();
                    using var document = JsonDocument.Parse(result);
                    // Get the root object
                    var root = document.RootElement;

                    // Assuming there's only one property at the root level (dynamic key)
                    if (!root.EnumerateObject().Any()) return null;

                    // Get the first property dynamically
                    var firstProperty = root.EnumerateObject().First();

                    // Deserialize the Metadata object
                    return JsonSerializer.Deserialize<Metadata>(firstProperty.Value.GetRawText());
                }
                else
                {
                    _log.Warning($"FetchGameMetaData: Error while fetching game assets {httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"RequestTokens: {ex.Message}");
                return null;
            }
        }

        public async Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
        {
            try
            {
                _log.Information("FetchGameAssets: Fetching game assets");
                var accessToken = await _authManager.GetAccessToken();

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpResponse = await _httpClient.GetAsync($"https://{LauncherHost}/launcher/api/public/assets/{platform}?label={label}");

                if (httpResponse.IsSuccessStatusCode)
                {
                    var result = await httpResponse.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<IEnumerable<Asset>>(result);
                }
                else
                {
                    _log.Error($"FetchGameAssets: Error while fetching game assets {httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
                    return null;
                }

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

        }

        public async Task<byte[]> GetGameManifest(GetManifestUrlData urlData)
        {

            foreach (var url in urlData.ManifestUrls)
            {
                _log.Information($"GetGameManifest: Trying to load manifests from {url}");

                try
                {
                    var httpResponse = await _httpClient.GetAsync(url);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        _log.Error($"Failed to fetch manifests from {url}, trying next url");
                        continue;
                    }
                    var result = await httpResponse.Content.ReadAsByteArrayAsync();
                    return result;
                }
                catch (Exception e)
                {
                    _log.Error($"Failed to fetch manifests from {url}, trying next url");
                }
            }
            return null;
        }

        public async Task DownloadFileAsync(string url, string destinationPath)
        {
            try
            {
                var accessToken = await _authManager.GetAccessToken();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"File {url} failed to download");

                // Create the directory if it doesn't exist
                var directoryPath = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to download file}", url);
            }
        }

        public async Task<string> GetGameToken()
        {
            try
            {
                var accessToken = await _authManager.GetAccessToken();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.GetAsync($"https://{OAuthHost}/account/api/oauth/exchange");
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                string responseContent = await reader.ReadToEndAsync();
                return responseContent;
            }
            catch (Exception ex)
            {
                _log.Error("GetGameToken: {@ex}", ex);
                return null;
            }
        }

        public async Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live")
        {
            try
            {
                _log.Information("GetGameManifest: Fetching game assets");
                var accessToken = await _authManager.GetAccessToken();

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpResponse = await _httpClient.GetAsync($"https://{LauncherHost}/launcher/api/public/assets/v2/platform/{platform}/namespace/{nameSpace}/catalogItem/{catalogItem}/app/{appName}/label/{label}");

                if (httpResponse.IsSuccessStatusCode)
                {
                    var result = await httpResponse.Content.ReadAsStringAsync();
                    var manifestUrlDatas = JsonSerializer.Deserialize<ManifestUrlData>(result);
                    if (manifestUrlDatas == null)
                    {
                        _log.Error($"GetGameManifest: Failed to parse manifest data from: {result}");
                        throw new Exception("Failed to retrieve manifest data");
                    }

                    if (manifestUrlDatas.Elements.Count > 1)
                    {
                        _log.Warning($"GetGameManifest: Multiple manifest urls found for {appName}");
                    }

                    var manifestUrls = new List<string>();
                    var baseUrls = new List<string>();

                    foreach (var urlData in manifestUrlDatas.Elements[0].Manifests)
                    {
                        var baseUrl = urlData.Uri.SubstringBeforeLast("/");
                        if (!baseUrls.Contains(baseUrl))
                        {
                            baseUrls.Add(baseUrl);
                        }

                        if (urlData.QueryParams != null)
                        {
                            var paramsString = string.Join("&", urlData.QueryParams.Select(p => $"{p.Name}={p.Value}"));
                            manifestUrls.Add($"{urlData.Uri}?{paramsString}");
                        }
                        else
                        {
                            manifestUrls.Add(urlData.Uri);
                        }
                    }

                    return new GetManifestUrlData()
                    {
                        BaseUrls = baseUrls,
                        ManifestUrls = manifestUrls,
                        ManifestHash = manifestUrlDatas.Elements[0].Hash,
                    };
                }
                else
                {
                    _log.Error($"GetGameManifest: Error while fetching game manifest {httpResponse.StatusCode} {httpResponse.ReasonPhrase}");
                    return null;
                }

            }
            catch (Exception e)
            {
                _log.Error(e.ToString());
                throw;
            }
        }
    }

    public class GetManifestUrlData
    {
        public List<string> BaseUrls { get; set; }
        public List<string> ManifestUrls { get; set; }
        public string ManifestHash { get; set; }
    }

    public static class StringExtensions
    {
        public static string SubstringBeforeLast(this string source, string delimiter)
        {
            var lastIndexOfDelimiter = source.LastIndexOf(delimiter, StringComparison.Ordinal);
            return lastIndexOfDelimiter == -1 ? source : source.Substring(0, lastIndexOfDelimiter);
        }
    }
}
