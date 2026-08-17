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
        ReconcileMissingInstallations();
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
        return ReconcileMissingInstallation(_storage.GetGameMetaData(name));
    }

    /// <summary>
    /// Get all DLC Game objects that belong to a base game
    /// </summary>
    public List<Game> GetDlcsForGame(string appName)
    {
        var baseGame = _storage.GetGameMetaData(appName);
        if (baseGame == null || baseGame.IsDlc()) return [];

        var baseGameCatalogId = baseGame.Metadata?.Id;
        if (string.IsNullOrEmpty(baseGameCatalogId)) return [];

        return _storage.GameMetaDataDictionary.Values
            .Where(g => g.IsDlc() && g.Metadata?.MainGameItem?.Id == baseGameCatalogId)
            .ToList();
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
                var metaData = _storage.GetGameMetaData(appName);
                if (metaData == null)
                {
                    _log.Warning("LaunchApp: Trying to launch game not owned {@game}", appName);
                    return;
                }

                if (metaData.LocalAppState?.InstallStatus != InstallState.Installed &&
                    metaData.LocalAppState?.InstallStatus != InstallState.NeedUpdate)
                {
                    Log.Warning("LaunchApp: Trying to launch game not installed");
                    return;
                }

                if (metaData.IsDlc())
                {
                    _log.Warning("LaunchApp: launching DLC's is not yet supported");
                    return;
                }

                var tokenResult = await _storeRepository.GetGameToken();
                if (!tokenResult.IsSuccess)
                {
                    _log.Error(
                        "LaunchApp: Game token request failed with {FailureKind}",
                        tokenResult.Failure!.Kind);
                    return;
                }

                var responseObject = JsonSerializer.Deserialize<GameTokenResponse>(tokenResult.Value);
                var userData = await _authManager.GetUserData();
                if (responseObject?.Code == null || userData == null)
                    return;

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
                    FileName = ManifestPath.ResolveUnderRoot(gameInfo.InstallPath, gameInfo.Executable),
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

                var assetsResult = await _storeRepository.FetchGameAssets(EpicPayloadPlatform.Windows);
                if (!assetsResult.IsSuccess || assetsResult.Value.Count == 0)
                {
                    _log.Error(
                        "GetLibraryData: Asset request failed with {FailureKind}",
                        assetsResult.Failure?.Kind);
                    return;
                }

                await _storage.SaveGameAssetsData(assetsResult.Value);
                gameAssetsList = assetsResult.Value.ToList();
            }

            var fetchList = new List<FetchListItem>();

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
                var metadataResult = await _storeRepository.FetchGameMetaData(
                    item.NameSpace,
                    item.CatalogItemId,
                    token);
                if (!metadataResult.IsSuccess)
                {
                    _log.Warning(
                        "Metadata request for {AppName} failed with {FailureKind}",
                        item.AppName,
                        metadataResult.Failure!.Kind);
                    return;
                }

                var gameMetaData = new Models.Game
                {
                    AppName = item.AppName,
                    AppTitle = metadataResult.Value.Title,
                    AssetInfos = new AssetInfos
                    {
                        Windows = gameAssetsList.First(asset => asset.AppName == item.AppName)
                    },
                    Metadata = metadataResult.Value
                };
                _storage.SaveMetaData(gameMetaData);
            });

            // Hydrate LocalAppState for all games and check for updates
            _storage.HydrateAllLocalAppStates();
            CheckForGameUpdates(gameAssetsList);


            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(_storage.GameMetaDataDictionary.Values.ToList());
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
        }
    }

    private void ReconcileMissingInstallations()
    {
        foreach (var game in _storage.GameMetaDataDictionary.Values)
            ReconcileMissingInstallation(game);
    }

    private Game? ReconcileMissingInstallation(Game? game)
    {
        var installation = game?.LocalAppState;
        if (game == null || installation == null || installation.InstallStatus == InstallState.NotInstalled)
            return game;
        if (!string.IsNullOrWhiteSpace(installation.InstallPath) && Directory.Exists(installation.InstallPath))
            return game;

        _log.Warning(
            "Installed game directory is missing for {AppName}; marking it not installed",
            game.AppName);
        installation.InstallStatus = InstallState.NotInstalled;
        installation.InstallPath = null;
        installation.Version = null;
        installation.Executable = null;
        game.LocalAppState = installation;
        _storage.AddToLocalAppState(game.AppName, installation);
        _storage.SaveMetaData(game);
        GameStatusUpdated?.Invoke(game);
        return game;
    }

    /// <summary>
    /// Compare installed game versions against latest asset versions
    /// and mark games that need updating
    /// </summary>
    private void CheckForGameUpdates(List<Asset> gameAssetsList)
    {
        foreach (var (appName, localAppState) in _storage.LocalAppStateDictionary)
        {
            if (localAppState.InstallStatus != InstallState.Installed)
                continue;

            var asset = gameAssetsList.FirstOrDefault(a => a.AppName == appName);
            if (asset == null) continue;

            if (!string.IsNullOrEmpty(localAppState.Version) &&
                localAppState.Version != asset.BuildVersion)
            {
                _log.Information("CheckForGameUpdates: {AppName} needs update ({OldVersion} -> {NewVersion})",
                    appName, localAppState.Version, asset.BuildVersion);
                localAppState.InstallStatus = InstallState.NeedUpdate;
                _storage.AddToLocalAppState(appName, localAppState);

                var game = _storage.GetGameMetaData(appName);
                if (game != null)
                    game.LocalAppState = localAppState;
            }
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
