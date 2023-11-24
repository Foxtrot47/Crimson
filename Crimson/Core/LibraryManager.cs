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
using Crimson.Repository;

namespace Crimson.Core;

public class LibraryManager
{
    private const string LauncherHost = "launcher-public-service-prod06.ol.epicgames.com";
    private const string CatalogHost = "catalog-public-service-prod06.ol.epicgames.com";
    private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    private readonly ILogger _log;
    private readonly IStoreRepository _storeRepository;
    private readonly AuthManager _authManager;
    private readonly Storage _storage;

    private static readonly HttpClient HttpClient;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    private DateTime _lastUpdateDateTime = DateTime.MinValue;

    static LibraryManager()
    {
        HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public LibraryManager(ILogger log, IStoreRepository repository, AuthManager authManager, Storage storage)
    {
        _log = log;
        _storeRepository = repository;
        _authManager = authManager;
        _storage = storage;
    }
    /// <summary>
    /// Public method to get library data, call UpdateLibraryData it's been more than 20 minutes since last update
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <returns></returns>
    public async Task<IEnumerable<Game>> GetLibraryData(bool forceUpdate = false)
    {
        // Only update library data if it's been more than 20 minutes since last update
        var dataNeedsUpdate = forceUpdate || (_lastUpdateDateTime == DateTime.MinValue) || (DateTime.Now - _lastUpdateDateTime > TimeSpan.FromMinutes(20));

        if (!dataNeedsUpdate)
            return _storage.GameMetaDataDictionary.Values.ToList();

        // Update the library data
        await UpdateLibraryData(forceUpdate);
        // Optionally, you can update the last update timestamp here
        _lastUpdateDateTime = DateTime.Now;

        return _storage.GameMetaDataDictionary.Values.ToList();
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <param name="updateAssets"></param>
    /// <returns></returns>
    private async Task UpdateLibraryData(bool forceUpdate = false, bool updateAssets = true)
    {
        try
        {
            var metadataUpdated = false;
            var gameAssets = await _storage.GetGameAssetsData();
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

                var game = _storage.GetGameMetaData(asset.AppName);
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
                _storage.SaveMetaData(gameMetaData);
            }

            gameMetaDataDictionary = _storage.GameMetaDataDictionary;

            // Sort _gameData by name
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(gameMetaDataDictionary.Values.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
        }
    }

    private async Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
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

    private async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
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
}

internal class FetchListItem
{
    public string AppName { get; set; }
    public string NameSpace { get; set; }
    public string CatalogItemId { get; set; }
}

