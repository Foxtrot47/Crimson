using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Crimson.Models;
using Serilog;
using Windows.Storage;
using Crimson.Utils;
using System.Net.Http.Headers;

namespace Crimson.Core;

public static class LibraryManager
{
    private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
    private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
    private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    // File contains game data
    private static string _gameDataFile;

    private static ILogger _log;

    private static readonly HttpClient HttpClient;

    public static event Action<ObservableCollection<Game>> LibraryUpdated;
    public static event Action<Game> GameStatusUpdated;


    static LibraryManager()
    {
        HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public static void Initialize(string binaryPath, ILogger log)
    {
        _log = log;


        var localFolder = ApplicationData.Current.LocalFolder;
        _gameDataFile = $@"{localFolder.Path}\gamedata.json";
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <param name="updateAssets"></param>
    /// <returns></returns>
    public static async Task GetLibraryData(bool forceUpdate = false, bool updateAssets = true)
    {
        try
        {
            var metadataUpdated = false;
            var gameAssets = await Storage.GetGameAssetsData();
            if (gameAssets == null)
            {
                _log.Information("UpdateLibraryData: No cached game assets found");
            }
            var gameAssetsList = gameAssets?.ToList() ?? new List<Asset>();
            if (forceUpdate || gameAssetsList.Count < 1)
            {
                _log.Error("UpdateLibraryData: No existing game assets data, updating");

                var assets = (await FetchGameAssets()).ToList();
                if (assets.Count < 1)
                {
                    _log.Error("GetLibraryData: Error while fetching game assets");
                    return;
                }
                gameAssetsList = assets.ToList();
            }

            var fetchList = new List<FetchListItem>();
            var gameMetaDataDictionary = new Dictionary<string, Models.Game>();

            foreach (var asset in gameAssetsList)
            {
                if (asset.Namespace.Contains("ue")) continue;

                var game = Storage.GetGameMetaData(asset.AppName);
                var assetUpdated = false;
                if (game != null)
                {
                    assetUpdated = asset.BuildVersion != game.AssetInfos.Windows.BuildVersion;
                    gameMetaDataDictionary.Add(asset.AppName, game);
                }

                if (!updateAssets || (game != null && !forceUpdate && !assetUpdated)) continue;
                _log.Information($"Scheduling metadata update for {asset.AppName}");
                fetchList.Add(new FetchListItem()
                {
                    AppName = asset.AppName,
                    NameSpace = asset.Namespace,
                    CatalogItemId = asset.CatalogItemId
                });
            }

            // Only update metadata if there are any updates or if forced
            foreach (var item in fetchList)
            {
                var egFetchGameMetaData = await FetchGameMetaData(item.NameSpace, item.CatalogItemId);

                // ignore if no metadata can be fetched
                if (egFetchGameMetaData == null) continue;
                var gameMetaData = new Models.Game()
                {
                    AppName = item.AppName,
                    AppTitle = egFetchGameMetaData.Title,
                    AssetInfos = new AssetInfos()
                    {
                        Windows = gameAssetsList.FirstOrDefault(asset => asset.AppName == item.AppName),
                    },
                    Metadata = egFetchGameMetaData
                };
                Storage.SaveMetaData(gameMetaData);
            }

            gameMetaDataDictionary = Storage.GameMetaDataDictionary;

            // Sort _gameData by name
            _log.Information("UpdateLibraryAsync: Library updated");
            //LibraryUpdated?.Invoke(_gameData);
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            return;
        }
    }

    private static async Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
    {
        try
        {
            _log.Information("FetchGameAssets: Fetching game assets");
            var accessToken = await AuthManager.GetAccessToken();

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

    private static async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
    {

        _log.Information("FetchGameMetaData: Fetching game metadata");
        var accessToken = await AuthManager.GetAccessToken();
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
}

internal class FetchListItem
{
    public string AppName { get; set; }
    public string NameSpace { get; set; }
    public string CatalogItemId { get; set; }
}

