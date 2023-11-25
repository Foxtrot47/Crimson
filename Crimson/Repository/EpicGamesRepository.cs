using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Crimson.Core;
using Crimson.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Crimson.Repository
{
    internal class EpicGamesRepository: IStoreRepository
    {
        private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
        private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
        private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

        private static readonly HttpClient HttpClient;
        private readonly ILogger _log;
        private readonly AuthManager _authManager;

        static EpicGamesRepository()
        {
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
        public EpicGamesRepository(AuthManager authManager, ILogger logger)
        {
            _log = logger;
            _authManager = authManager;
        }
        public async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {

            _log.Information("FetchGameMetaData: Fetching game metadata");
            var accessToken = await _authManager.GetAccessToken();
            HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // API requests parameters to be in query instead of body
            var qs = $"?id={catalogItemId}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en";

            try
            {
                // Make the API call with the form data
                var httpResponse = await HttpClient.GetAsync($"https://{CatalogHost}/catalog/api/shared/namespace/{nameSpace}/bulk/items{qs}");
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

                HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var httpResponse = await HttpClient.GetAsync($"https://{LauncherHost}/launcher/api/public/assets/{platform}?label={label}");

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

    }
}
