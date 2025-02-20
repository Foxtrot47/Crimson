using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public class LibraryManager
{
    private readonly ILogger _log;
    private readonly IStoreRepository _storeRepository;
    private readonly Storage _storage;
    private readonly AuthManager _authManager;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    private DateTime _lastUpdateDateTime = DateTime.MinValue;

    public LibraryManager(ILogger log, IStoreRepository repository, Storage storage, AuthManager authManager)
    {
        _log = log;
        _storeRepository = repository;
        _storage = storage;
        _authManager = authManager;
    }

    /// <summary>
    /// Public method to get library data, call UpdateLibraryData it's been more than 20 minutes since last update
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <returns></returns>
    public async Task<IEnumerable<Game>> GetLibraryData(bool forceUpdate = false)
    {
        // Only update library data if it's been more than 20 minutes since last update
        var dataNeedsUpdate = forceUpdate || (_lastUpdateDateTime == DateTime.MinValue) ||
                              (DateTime.Now - _lastUpdateDateTime > TimeSpan.FromMinutes(20));

        if (!dataNeedsUpdate)
            return _storage.GameMetaDataDictionary.Values.ToList();

        // Update the library data
        await UpdateLibraryData(forceUpdate);
        // Optionally, you can update the last update timestamp here
        _lastUpdateDateTime = DateTime.Now;

        return _storage.GameMetaDataDictionary.Values.ToList();
    }

    public Game GetGameInfo(string name)
    {
        return _storage.GetGameMetaData(name);
    }

    /// <summary>
    /// Updates stored game data and fired GameStatusUpdated event
    /// Only thing that would call this function would be InstallManager
    /// </summary>
    /// <param name="game"></param>
    public void UpdateGameInfo(Game game)
    {
        _storage.SaveMetaData(game);
        GameStatusUpdated?.Invoke(game);
    }

    public async Task LaunchApp(string appName)
    {
        try
        {
            if (appName == null) return;

            _log.Information("LaunchApp: Trying to launch app: {@appName}", appName);

            if (_storage.LocalAppStateDictionary.TryGetValue(appName, out var gameInfo))
            {
                await UpdateLibraryData(false, true);
                var metaData = _storage.GetGameMetaData(appName);
                if (metaData == null)
                {
                    _log.Warning("LaunchApp: Trying to launch game not owned {@game}", appName);
                    return;
                }

                if (metaData.InstallStatus != InstallState.Installed)
                {
                    Log.Warning("LaunchApp: Trying to launch game not installed");
                }

                if (metaData.IsDlc())
                {
                    _log.Warning("LaunchApp: launching DLC's is not yet supported");
                    return;
                }

                if (metaData.AssetInfos.Windows.BuildVersion != gameInfo.Version)
                {
                    _log.Warning("LaunchApp: Trying to launch out dated game");

                    // Don't disallow launching out of date games until we implement updating mechanism
                    // return;
                }

                var responseData = await _storeRepository.GetGameToken();
                var responseObject = JsonSerializer.Deserialize<GameTokenResponse>(responseData);
                var userData = await _authManager.GetUserData();

                var parameters = new List<string>();
                parameters.Add($"-AUTH_LOGIN=unused");
                parameters.Add($"-AUTH_PASSWORD={responseObject.Code}");
                parameters.Add("-AUTH_TYPE=exchangecode");
                parameters.Add($"-epicapp={gameInfo.AppName}");
                parameters.Add("-epicenv=Prod");

                parameters.Add("-EpicPortal");
                parameters.Add($"-epicusername=\"{userData.DisplayName}\"");
                parameters.Add($"-epicuserid={userData.AccountId}");
                parameters.Add($"-epicsandboxid={metaData.AssetInfos.Windows.Namespace}");
                parameters.Add("-epiclocale=en");

                string arguments = string.Join(" ", parameters);

                // Create a new process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Join(gameInfo.InstallPath, gameInfo.Executable),
                    Arguments = arguments,
                    UseShellExecute = false,
                    WorkingDirectory = gameInfo.InstallPath
                };

                // Create and start the process
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                process.WaitForExit();
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Fatal("LaunchApp: Exception: {@ex}", ex);
        }
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

                var assets = (await _storeRepository.FetchGameAssets()).ToList();
                if (assets.Count < 1)
                {
                    _log.Error("GetLibraryData: Error while fetching game assets");
                    return;
                }
                await _storage.SaveGameAssetsData(assets);
                gameAssetsList = assets.ToList();
            }

            var fetchList = new List<FetchListItem>();
            var gameMetaDataDictionary = new Dictionary<string, Models.Game>();

            foreach (var asset in gameAssetsList)
            {
                // skip adding unreal engine assets
                var pattern = @".*UE.*Windows";

                // Check if the asset namespace or build version contains the pattern
                if (asset.Namespace.Contains("ue") || Regex.IsMatch(asset.BuildVersion, pattern))
                {
                    continue;
                }

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

            var options = new ParallelOptions()
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };
            // Only update metadata if there are any updates or if forced
            await Parallel.ForEachAsync(fetchList, options, async (item, token) =>
            {
                var egFetchGameMetaData = await _storeRepository.FetchGameMetaData(item.NameSpace, item.CatalogItemId);

                // ignore if no metadata can be fetched
                if (egFetchGameMetaData == null) return;
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
            });

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
}

internal class FetchListItem
{
    public string AppName { get; set; }
    public string NameSpace { get; set; }
    public string CatalogItemId { get; set; }
}

public class GameTokenResponse
{
    [JsonPropertyName("expiresInSeconds")] public int ExpiresInSeconds { get; set; }

    [JsonPropertyName("code")] public string Code { get; set; }

    [JsonPropertyName("creatingClientId")] public string CreatingClientId { get; set; }
}
