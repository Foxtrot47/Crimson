using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
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
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private IReadOnlySet<string> _ownedAppNames = new HashSet<string>();

    public LibraryManager(ILogger log, IStoreRepository repository, Storage storage, AuthManager authManager)
    {
        _log = log;
        _storeRepository = repository;
        _storage = storage;
        _authManager = authManager;
        _authManager.AuthStatusChanged += OnAuthStatusChanged;
    }

    private void OnAuthStatusChanged(object sender, AuthStatusChangedEventArgs e)
    {
        if (e.NewStatus != AuthenticationStatus.LoggedOut)
            return;

        _ownedAppNames = new HashSet<string>();
        InvalidateCache();
    }

    public void InvalidateCache()
    {
        _lastUpdateDateTime = DateTime.MinValue;
    }

    /// <summary>
    /// Public method to get library data, call UpdateLibraryData it's been more than 20 minutes since last update
    /// </summary>
    /// <param name="forceUpdate"></param>
    /// <returns></returns>
    public async Task<IEnumerable<Game>> GetLibraryData(bool forceUpdate = false)
    {
        await _updateGate.WaitAsync();
        try
        {
            var dataNeedsUpdate = forceUpdate || _lastUpdateDateTime == DateTime.MinValue ||
                                  DateTime.Now - _lastUpdateDateTime > TimeSpan.FromMinutes(20);
            if (!dataNeedsUpdate)
                return GetOwnedGames();

            var updatedLibrary = await UpdateLibraryData(refreshAssets: true, forceMetadataUpdate: forceUpdate);
            if (updatedLibrary is not null)
            {
                _ownedAppNames = updatedLibrary.Select(game => game.AppName).ToHashSet(StringComparer.Ordinal);
                _lastUpdateDateTime = DateTime.Now;
            }

            return GetOwnedGames();
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private List<Game> GetOwnedGames() => _storage.GameMetaDataDictionary
        .Where(entry => _ownedAppNames.Contains(entry.Key))
        .Select(entry => entry.Value)
        .ToList();

    public Game GetGameInfo(string name)
    {
        return _storage.GetGameMetaData(name);
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

            _log.Information("LaunchApp: Trying to launch app: {AppName}", appName);

            if (_storage.LocalAppStateDictionary.TryGetValue(appName, out var gameInfo))
            {
                var metaData = _storage.GetGameMetaData(appName);
                if (metaData == null)
                {
                    _log.Warning("LaunchApp: Trying to launch game not owned {AppName}", appName);
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
            _log.Error(ex, "LaunchApp failed");
        }
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    /// <param name="refreshAssets"></param>
    /// <param name="forceMetadataUpdate"></param>
    /// <returns></returns>
    private async Task<IReadOnlyList<Game>?> UpdateLibraryData(bool refreshAssets, bool forceMetadataUpdate)
    {
        try
        {
            var gameAssets = await GetGameAssetsAsync(refreshAssets);
            if (gameAssets is null)
                return null;

            var fetchList = GetMetadataFetchList(gameAssets, forceMetadataUpdate);
            await FetchMetadataAsync(fetchList, gameAssets);

            _storage.HydrateAllLocalAppStates();
            CheckForGameUpdates(gameAssets);

            var ownedGames = SelectOwnedGames(gameAssets, _storage.GameMetaDataDictionary);
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(ownedGames);
            return ownedGames;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "UpdateLibraryData failed");
            return null;
        }
    }

    private async Task<List<Asset>?> GetGameAssetsAsync(bool refreshAssets)
    {
        var cachedAssets = (await _storage.GetGameAssetsData())?.ToList() ?? [];
        if (!refreshAssets && cachedAssets.Count > 0)
            return cachedAssets;

        _log.Information("UpdateLibraryData: Refreshing game assets");
        var assets = (await _storeRepository.FetchGameAssets())?.ToList();
        if (assets is null || assets.Count == 0)
        {
            _log.Error("GetLibraryData: Error while fetching game assets");
            return null;
        }

        await _storage.SaveGameAssetsData(assets);
        return assets;
    }

    private List<FetchListItem> GetMetadataFetchList(
        IReadOnlyList<Asset> gameAssets,
        bool forceMetadataUpdate)
    {
        var fetchList = new List<FetchListItem>();
        foreach (var asset in gameAssets)
        {
            if (IsUnrealEngineAsset(asset))
                continue;

            var game = _storage.GetGameMetaData(asset.AppName);
            var assetUpdated = game is not null &&
                asset.BuildVersion != game.AssetInfos.Windows.BuildVersion;
            if (game is not null && !forceMetadataUpdate && !assetUpdated)
                continue;

            _log.Debug("Scheduling metadata update for {AppName}", asset.AppName);
            fetchList.Add(new FetchListItem
            {
                AppName = asset.AppName,
                NameSpace = asset.Namespace,
                CatalogItemId = asset.CatalogItemId
            });
        }

        return fetchList;
    }

    private async Task FetchMetadataAsync(
        IEnumerable<FetchListItem> fetchList,
        IReadOnlyList<Asset> gameAssets)
    {
        var fetchedGames = new ConcurrentBag<Game>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(fetchList, options, async (item, _) =>
        {
            var metadata = await _storeRepository.FetchGameMetaData(item.NameSpace, item.CatalogItemId);
            if (metadata is null)
                return;

            fetchedGames.Add(new Game
            {
                AppName = item.AppName,
                AppTitle = metadata.Title,
                AssetInfos = new AssetInfos
                {
                    Windows = gameAssets.First(asset => asset.AppName == item.AppName)
                },
                Metadata = metadata
            });
        });

        foreach (var game in fetchedGames)
            _storage.SaveMetaData(game);
    }

    private static bool IsUnrealEngineAsset(Asset asset) =>
        asset.Namespace.Contains("ue") || Regex.IsMatch(asset.BuildVersion, @".*UE.*Windows");

    internal static IReadOnlyList<Game> SelectOwnedGames(
        IEnumerable<Asset> assets,
        IReadOnlyDictionary<string, Game> metadata)
    {
        var ownedAppNames = assets.Select(asset => asset.AppName).ToHashSet(StringComparer.Ordinal);
        return metadata
            .Where(entry => ownedAppNames.Contains(entry.Key))
            .Select(entry => entry.Value)
            .ToList();
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
