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
    // ObservableCollection to store the game info objects and subscribe to its events
    private static ObservableCollection<Game> _gameData;
    // Timer to trigger the file update periodically
    private static Timer _timer;
    private static ILogger _log;
    private static string _legendaryBinaryPath;

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
        _legendaryBinaryPath = binaryPath;
        _log = log;


        var localFolder = ApplicationData.Current.LocalFolder;
        _gameDataFile = $@"{localFolder.Path}\gamedata.json";

        // If stored data exists load it
        if (File.Exists(_gameDataFile))
        {
            try
            {
                using var data = File.Open(_gameDataFile, FileMode.Open);
                _gameData = JsonSerializer.Deserialize<ObservableCollection<Game>>(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
        // Else create new data and save it
        else
            _gameData = new ObservableCollection<Game>();
    }

    // Declare a method to update the JSON file when the data changes
    // Method must be public as it should be called when program is being terminated
    // ReSharper disable once MemberCanBePrivate.Global
    public static async Task UpdateJsonFileAsync()
    {
        try
        {
            // Serialize the games list to a JSON string
            var jsonString = JsonSerializer.Serialize(_gameData);

            //await using var fileStream = File.Open(_gameDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            //await using var streamWriter = new StreamWriter(fileStream);
            //await streamWriter.WriteAsync(jsonString);
            //await streamWriter.FlushAsync();
        }
        catch (Exception exception)
        {
            _log.Error("UpdateJsonFile: Error while updating json {Exception}", exception.ToString());
        }
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <param name="updateAssets"></param>
    /// <returns></returns>
    public static async Task<ObservableCollection<Game>> GetLibraryData(bool forceUpdate = false, bool updateAssets = true)
    {
        try
        {
            var metadataUpdated = false;
            var gameAssets = await Storage.GetGameAssetsData();
            if (gameAssets == null)
            {
                _log.Information("GetLibraryData: No cached game assets found");
            }
            var gameAssetsList = gameAssets?.ToList() ?? new List<Asset>();
            if (forceUpdate || gameAssetsList.Count < 1)
            {
                _log.Error("GetLibraryData: No existing game assets data, updating");

                var assets = (await FetchGameAssets()).ToList();
                if (assets.Count < 1)
                {
                    _log.Error("GetLibraryData: Error while fetching game assets");
                    return null;
                }
                gameAssetsList = assets.ToList();
            }

            var fetchList = new List<FetchListItem>();
            var gameMetaDataDictionary = new Dictionary<string, GameMetaData>();

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

                if (!updateAssets || (game != null && !forceUpdate && (game == null || !assetUpdated))) continue;
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
                var gameMetaData = new GameMetaData()
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
            foreach (var item in gameMetaDataDictionary)
            {
                var newGame = new Game()
                {
                    Name = item.Value.AppName,
                    Title = item.Value.Metadata.Title,
                    State = Game.InstallState.NotInstalled,
                    Images = item.Value.Metadata.KeyImages.Select(image => new Game.Image()
                    {
                        Height = image.Height,
                        Type = image.Type,
                        Url = image.Url,
                        Width = image.Width
                    }).ToList(),
                    Version = item.Value.AssetInfos.Windows.BuildVersion
                };

                // Do not add DLC's to gameData
                if (item.Value.Metadata.MainGameItem != null)
                    continue;

                // Remove existing data if present
                _gameData.Remove(newGame);
                _gameData.Add(newGame);
            }
            // Sort _gameData by name
            _gameData = new ObservableCollection<Game>(_gameData.OrderBy(game => game.Title));
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(_gameData);
            return _gameData;
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            return null;
        }
    }

    public static Game GetGameInfo(string name)
    {
        return _gameData.FirstOrDefault(game => game.Name == name);
    }

    public static void AddToInstallationQueue(string gameName, ActionType actionType, string location)
    {
        var game = _gameData.FirstOrDefault(g => g.Name == gameName);
        if (game == null) return;

        if (actionType == ActionType.Install)
            game.State = Game.InstallState.Installing;

        GameStatusUpdated?.Invoke(game);
        InstallManager.AddToQueue(new InstallItem(gameName, actionType, location));
        _log.Information("AddToInstallationQueue: {Game} {Action} {location}", gameName, actionType, location);
    }

    public static void FinishedInstall(InstallItem item)
    {
        var game = _gameData.FirstOrDefault(game => game.Name == item.AppName);
        if (game == null) return;

        _log.Information("FinishedInstall: {Game} {Action} {Status}", item.AppName, item.Action, item.Status);
        if (item.Action == ActionType.Uninstall && item.Status == ActionStatus.Success)
            game.State = Game.InstallState.NotInstalled;

        else if (item.Action is ActionType.Install or ActionType.Update or ActionType.Repair && item.Status == ActionStatus.Success)
            game.State = Game.InstallState.Installed;

        else if (item.Action == ActionType.Install && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.NotInstalled;

        else if (item.Action == ActionType.Update && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.NeedUpdate;

        else if (item.Action == ActionType.Repair && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.Broken;

        GameStatusUpdated?.Invoke(game);
    }
    public static Game GetGameData(string gameName)
    {
        var legendaryHandle = new Legendary(_legendaryBinaryPath, _log);
        var data = legendaryHandle.GetGameData(gameName);
        var game = _gameData.FirstOrDefault(game => game.Name == gameName);
        game.DownloadSizeMiB = data.DownloadSizeMiB;
        game.DiskSizeMiB = data.DiskSizeMiB;
        _log.Information("GetGameData: {Game} {DownloadSizeMiB} {DiskSizeMiB}", gameName, data.DownloadSizeMiB, data.DiskSizeMiB);
        ;
        return game;
    }

    // Stop the timer when the application exits
    public static void Dispose()
    {
        _timer.Stop();
    }
    public static void StartGame(string name)
    {
        var game = _gameData.FirstOrDefault(game => game.Name == name);
        if (game == null) return;

        var legendaryHandle = new Legendary(_legendaryBinaryPath, _log);
        legendaryHandle.StartGame(game.Name);
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

public class MetaDataEndPointResponse
{
    public Dictionary<string, Metadata> MetadataDictionary { get; set; }
}

