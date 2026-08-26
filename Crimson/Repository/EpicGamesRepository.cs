using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        private const string StoreSearchUrl = "https://launcher.store.epicgames.com/graphql";
        private const string StoreSearchQuery = """
            query searchStoreQuery(
                $count: Int,
                $country: String!,
                $keywords: String,
                $locale: String,
                $sortBy: String,
                $sortDir: String,
                $start: Int
            ) {
                Catalog {
                    searchStore(
                        count: $count,
                        country: $country,
                        keywords: $keywords,
                        locale: $locale,
                        sortBy: $sortBy,
                        sortDir: $sortDir,
                        start: $start
                    ) {
                        elements {
                            title
                            keyImages { type url }
                            productSlug
                            urlSlug
                            catalogNs { mappings(pageType: "productHome") { pageSlug } }
                            offerMappings { pageSlug }
                        }
                    }
                }
            }
            """;

        private readonly HttpClient _apiClient;
        private readonly HttpClient _contentClient;
        private readonly HttpClient _storeClient;
        private readonly ILogger _log;
        private readonly AuthManager _authManager;

        public EpicGamesRepository(
            AuthManager authManager,
            ILogger logger,
            HttpClient apiClient,
            HttpClient contentClient,
            HttpClient storeClient)
        {
            _log = logger;
            _authManager = authManager;
            _apiClient = apiClient;
            _contentClient = contentClient;
            _storeClient = storeClient;
        }

        public async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {
            _log.Information("FetchGameMetaData: Fetching game metadata");
            var accessToken = await _authManager.GetAccessToken();
            var uri = $"https://{CatalogHost}/catalog/api/shared/namespace/{Uri.EscapeDataString(nameSpace)}/bulk/items?id={Uri.EscapeDataString(catalogItemId)}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en";

            try
            {
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, uri, accessToken);
                using var response = await _apiClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning(
                        "FetchGameMetaData failed with HTTP {StatusCode} {ReasonPhrase}",
                        (int)response.StatusCode,
                        response.ReasonPhrase);
                    return null;
                }

                var result = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(result);
                var firstProperty = document.RootElement.EnumerateObject().FirstOrDefault();
                return firstProperty.Value.ValueKind == JsonValueKind.Undefined
                    ? null
                    : JsonSerializer.Deserialize<Metadata>(firstProperty.Value.GetRawText());
            }
            catch (Exception ex)
            {
                _log.Error("FetchGameMetaData failed with {ErrorType}", ex.GetType().Name);
                return null;
            }
        }

        public async Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
        {
            _log.Information("FetchGameAssets: Fetching game assets");
            var accessToken = await _authManager.GetAccessToken();
            var uri = $"https://{LauncherHost}/launcher/api/public/assets/{Uri.EscapeDataString(platform)}?label={Uri.EscapeDataString(label)}";

            try
            {
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, uri, accessToken);
                using var response = await _apiClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Error(
                        "FetchGameAssets failed with HTTP {StatusCode} {ReasonPhrase}",
                        (int)response.StatusCode,
                        response.ReasonPhrase);
                    return null;
                }

                var result = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<IEnumerable<Asset>>(result);
            }
            catch (Exception ex)
            {
                _log.Error("FetchGameAssets failed with {ErrorType}", ex.GetType().Name);
                throw;
            }
        }

        public async Task<IReadOnlyList<StoreSearchResult>> SearchStore(
            string query,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return [];

            var payload = JsonSerializer.Serialize(new
            {
                query = StoreSearchQuery,
                variables = new
                {
                    count = 5,
                    country = "US",
                    keywords = query.Trim(),
                    locale = "en-US",
                    sortBy = "relevancy",
                    sortDir = "DESC",
                    start = 0
                }
            });

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    EpicEndpointPolicy.RequireStoreUri(StoreSearchUrl))
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                using var response = await _storeClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning("SearchStore failed with HTTP {StatusCode}", (int)response.StatusCode);
                    return [];
                }

                var result = await response.Content.ReadAsStringAsync(cancellationToken);
                return StoreSearchResultParser.Parse(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "SearchStore failed");
                return [];
            }
        }

        public async Task<byte[]> GetGameManifest(GetManifestUrlData urlData)
        {
            foreach (var value in urlData.ManifestUrls)
            {
                try
                {
                    var uri = EpicEndpointPolicy.RequireContentUri(value);
                    _log.Information(
                        "GetGameManifest: Trying content endpoint {ManifestUri}",
                        SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri));
                    using var response = await _contentClient.GetAsync(uri);
                    if (!response.IsSuccessStatusCode)
                    {
                        _log.Error(
                            "GetGameManifest failed with HTTP {StatusCode}; trying next endpoint",
                            (int)response.StatusCode);
                        continue;
                    }

                    return await response.Content.ReadAsByteArrayAsync();
                }
                catch (Exception ex)
                {
                    _log.Error(
                        "GetGameManifest endpoint failed with {ErrorType}; trying next endpoint",
                        ex.GetType().Name);
                }
            }

            return null;
        }

        public async Task DownloadFileAsync(string url, string destinationPath)
        {
            try
            {
                var uri = EpicEndpointPolicy.RequireContentUri(url);
                using var response = await _contentClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                var directoryPath = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directoryPath))
                    Directory.CreateDirectory(directoryPath);

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await stream.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                _log.Error("DownloadFile failed with {ErrorType}", ex.GetType().Name);
            }
        }

        public async Task<string> GetGameToken()
        {
            try
            {
                var accessToken = await _authManager.GetAccessToken();
                using var request = CreateAuthenticatedRequest(
                    HttpMethod.Get,
                    $"https://{OAuthHost}/account/api/oauth/exchange",
                    accessToken);
                using var response = await _apiClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _log.Error("GetGameToken failed with {ErrorType}", ex.GetType().Name);
                return null;
            }
        }

        public async Task<GetManifestUrlData> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            string platform = "Windows",
            string label = "Live")
        {
            try
            {
                _log.Information("GetGameManifest: Fetching manifest metadata");
                var accessToken = await _authManager.GetAccessToken();
                var uri = $"https://{LauncherHost}/launcher/api/public/assets/v2/platform/{Uri.EscapeDataString(platform)}/namespace/{Uri.EscapeDataString(nameSpace)}/catalogItem/{Uri.EscapeDataString(catalogItem)}/app/{Uri.EscapeDataString(appName)}/label/{Uri.EscapeDataString(label)}";
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, uri, accessToken);
                using var response = await _apiClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Error(
                        "GetGameManifest metadata failed with HTTP {StatusCode} {ReasonPhrase}",
                        (int)response.StatusCode,
                        response.ReasonPhrase);
                    return null;
                }

                var result = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<ManifestUrlData>(result);
                if (data?.Elements == null || data.Elements.Count == 0 || data.Elements[0].Manifests == null)
                {
                    _log.Error("GetGameManifest returned invalid manifest metadata");
                    return null;
                }

                if (data.Elements.Count > 1)
                    _log.Warning("GetGameManifest returned multiple manifest entries for {AppName}", appName);

                var manifestUrls = new List<string>();
                var baseUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in data.Elements[0].Manifests)
                {
                    // Skip mirrors on hosts outside the allowlist instead of throwing.
                    // Epic rotates CDNs, and one unrecognised host used to abort the whole
                    // manifest-URL resolution and leave the install with no mirrors at all.
                    if (!Uri.TryCreate(entry.Uri, UriKind.Absolute, out var contentUri) ||
                        !EpicEndpointPolicy.IsAllowedContentUri(contentUri))
                    {
                        _log.Warning(
                            "Rejected unapproved Epic CDN host {Host}",
                            contentUri?.Host ?? "invalid");
                        continue;
                    }

                    var builder = new UriBuilder(contentUri);
                    if (entry.QueryParams is { Count: > 0 })
                    {
                        // Emitted verbatim. These carry the CDN signature and Epic already
                        // delivers them encoded; re-escaping turns %2f into %252f and the CDN
                        // rejects the request. legendary does the same (core.py, queryParams).
                        builder.Query = string.Join(
                            "&",
                            entry.QueryParams.Select(parameter =>
                                $"{parameter.Name}={parameter.Value}"));
                    }

                    manifestUrls.Add(builder.Uri.AbsoluteUri);
                    var lastSlash = contentUri.AbsolutePath.LastIndexOf('/');
                    if (lastSlash > 0)
                    {
                        var baseBuilder = new UriBuilder(contentUri)
                        {
                            Path = contentUri.AbsolutePath[..lastSlash],
                            Query = string.Empty,
                            Fragment = string.Empty
                        };
                        baseUrls.Add(baseBuilder.Uri.AbsoluteUri.TrimEnd('/'));
                    }
                }

                return new GetManifestUrlData
                {
                    BaseUrls = baseUrls.ToList(),
                    ManifestUrls = manifestUrls,
                    ManifestHash = data.Elements[0].Hash
                };
            }
            catch (Exception ex)
            {
                _log.Error("GetManifestUrls failed with {ErrorType}", ex.GetType().Name);
                throw;
            }
        }

        private static HttpRequestMessage CreateAuthenticatedRequest(
            HttpMethod method,
            string value,
            string accessToken)
        {
            var request = new HttpRequestMessage(method, EpicEndpointPolicy.RequireApiUri(value));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }
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
