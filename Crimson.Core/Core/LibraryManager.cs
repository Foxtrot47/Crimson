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

    internal int AccountGeneration => _storage.AccountGeneration;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    private DateTimeOffset _lastUpdateDateTime = DateTimeOffset.MinValue;
    private int _cacheRevision;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private IReadOnlySet<string> _ownedAppNames = new HashSet<string>();
    private readonly ConcurrentDictionary<string, StoreProductCacheEntry> _storeProductCache = new();
    private readonly Dictionary<string, long> _storeProductFetchSequences = new();
    private readonly object _storeProductCacheLock = new();
    private int _storeProductCacheGeneration;
    private int _ownedAccountGeneration = -1;
    private readonly SemaphoreSlim _playtimeGate = new(1, 1);
    private readonly Dictionary<string, (long Seconds, DateTimeOffset ExpiresAt)> _playtimeCache = new();
    private int _playtimeGeneration;
    private readonly object _operationLock = new();
    private readonly Dictionary<string, Guid> _activeOperationApps = new(StringComparer.Ordinal);

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

        lock (_storage.AccountDataLock)
        {
            _ownedAppNames = new HashSet<string>();
            _ownedAccountGeneration = -1;
            _lastUpdateDateTime = DateTimeOffset.MinValue;
            InvalidateStoreProductCache();
            InvalidatePlaytimeCache();
        }
        LibraryUpdated?.Invoke([]);
    }

    public void InvalidateCache()
    {
        lock (_storage.AccountDataLock)
        {
            _cacheRevision++;
            _lastUpdateDateTime = DateTimeOffset.MinValue;
            _storage.InvalidateLibraryCache();
            InvalidateStoreProductCache();
            InvalidatePlaytimeCache();
        }
    }

    private void InvalidatePlaytimeCache()
    {
        _playtimeGeneration++;
        _playtimeCache.Clear();
    }

    public async Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default)
    {
        int generation;
        int accountGeneration;
        lock (_storage.AccountDataLock)
        {
            if (_ownedAccountGeneration != _storage.AccountGeneration || !_ownedAppNames.Contains(appName))
                return null;
            generation = _playtimeGeneration;
            accountGeneration = _storage.AccountGeneration;
        }

        await _playtimeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_storage.AccountDataLock)
            {
                if (generation != _playtimeGeneration || accountGeneration != _storage.AccountGeneration)
                    return null;
                if (_playtimeCache.TryGetValue(appName, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                    return cached.Seconds;
            }

            var seconds = await _storeRepository.FetchGamePlaytimeSeconds(appName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_storage.AccountDataLock)
            {
                if (generation != _playtimeGeneration || accountGeneration != _storage.AccountGeneration)
                    return null;
                if (seconds.HasValue)
                    _playtimeCache[appName] = (seconds.Value, DateTimeOffset.UtcNow.AddMinutes(5));
                return seconds;
            }
        }
        finally
        {
            _playtimeGate.Release();
        }
    }

    private void InvalidateStoreProductCache()
    {
        lock (_storeProductCacheLock)
        {
            _storeProductCacheGeneration++;
            _storeProductFetchSequences.Clear();
            _storeProductCache.Clear();
        }
    }

    public async Task<StoreProductDetails?> GetStoreProductDetails(
        string nameSpace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameSpace))
            return null;

        int generation;
        long fetchSequence;
        lock (_storeProductCacheLock)
        {
            if (_storeProductCache.TryGetValue(nameSpace, out var cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached.Details;
            generation = _storeProductCacheGeneration;
            _storeProductFetchSequences.TryGetValue(nameSpace, out fetchSequence);
            _storeProductFetchSequences[nameSpace] = ++fetchSequence;
        }

        var details = await _storeRepository.FetchStoreProductDetails(nameSpace, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (details is null) return null;

        lock (_storeProductCacheLock)
        {
            if (generation == _storeProductCacheGeneration &&
                _storeProductFetchSequences.GetValueOrDefault(nameSpace) == fetchSequence)
            {
                _storeProductCache[nameSpace] = new StoreProductCacheEntry(
                    details,
                    DateTimeOffset.UtcNow.AddHours(24));
            }
        }
        return details;
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
            int generation;
            int revision;
            lock (_storage.AccountDataLock)
            {
                generation = _storage.AccountGeneration;
                revision = _cacheRevision;
                if (_ownedAccountGeneration != generation)
                    RestoreCachedLibrary(generation);
                if (!forceUpdate && _ownedAccountGeneration == generation &&
                    DateTimeOffset.UtcNow - _lastUpdateDateTime <= TimeSpan.FromMinutes(20))
                    return GetOwnedGames();
            }

            await UpdateLibraryData(generation, revision, forceUpdate);
            return GetOwnedGames();
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void RestoreCachedLibrary(int generation)
    {
        var snapshot = _storage.ReadLibraryCache();
        if (snapshot is null) return;

        _ownedAppNames = snapshot.Assets.Select(asset => asset.AppName).ToHashSet(StringComparer.Ordinal);
        _ownedAccountGeneration = generation;
        _storage.HydrateAllLocalAppStates();
        CheckForGameUpdates(snapshot.Assets);
        _lastUpdateDateTime = _cacheRevision == 0 && GetMetadataFetchList(snapshot.Assets, false).Count == 0
            ? snapshot.UpdatedAt : DateTimeOffset.MinValue;
        LibraryUpdated?.Invoke(GetOwnedGames());
    }

    internal List<Game> GetOwnedGames()
    {
        lock (_storage.AccountDataLock)
        {
            if (_ownedAccountGeneration != _storage.AccountGeneration)
                return [];

            return _storage.GameMetaDataDictionary
                .Where(entry => _ownedAppNames.Contains(entry.Key))
                .Select(entry => entry.Value)
                .ToList();
        }
    }

    public Game GetGameInfo(string name)
    {
        return _storage.GetGameMetaData(name);
    }

    internal IReadOnlyList<Game> GetSidebarGames(IEnumerable<Game> cachedGames, string? activeAppName) =>
        cachedGames
            .Select(game => GetGameInfo(game.AppName))
            .OfType<Game>()
            .Where(game => game.Metadata is not null && !game.IsDlc() && IsSidebarListed(game, activeAppName))
            .ToList();

    internal static bool IsSidebarListed(Game game, string? activeAppName) =>
        string.Equals(game.AppName, activeAppName, StringComparison.Ordinal) ||
        game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate
            or InstallState.Broken or InstallState.Playing
            or InstallState.Installing or InstallState.InstallationPaused
            or InstallState.Updating or InstallState.UpdatingPaused or InstallState.Repairing;

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
            .Select(g => _storage.GetGameMetaData(g.AppName))
            .ToList();
    }

    public IReadOnlyList<Game> GetOwnedDlcsForGame(string appName)
    {
        lock (_storage.AccountDataLock)
        {
            if (_ownedAccountGeneration != _storage.AccountGeneration || !_ownedAppNames.Contains(appName))
                return [];
            return GetDlcsForGame(appName).Where(game => _ownedAppNames.Contains(game.AppName)).ToList();
        }
    }

    public string GetBaseGameAppName(string appName)
    {
        var game = _storage.GetGameMetaData(appName);
        var mainGameId = game?.Metadata?.MainGameItem?.Id;
        if (string.IsNullOrWhiteSpace(mainGameId)) return appName;

        var baseGame = _storage.GameMetaDataDictionary.Values.FirstOrDefault(candidate =>
            !candidate.IsDlc() &&
            string.Equals(candidate.Metadata?.Id, mainGameId, StringComparison.Ordinal));
        return baseGame?.AppName ?? appName;
    }

    public bool IsOperationForGame(string operationAppName, string gameAppName) =>
        string.Equals(
            GetBaseGameAppName(operationAppName),
            gameAppName,
            StringComparison.Ordinal);

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

    public async Task<bool> LaunchApp(string appName)
    {
        string? ownershipTokenPath = null;
        try
        {
            var launchContext = GetLaunchContext(appName);
            if (launchContext is null)
                return false;

            var (gameInfo, game) = launchContext.Value;
            var credentials = await GetLaunchCredentialsAsync(appName);
            if (credentials is null)
                return false;

            if (gameInfo.RequiresOt)
                ownershipTokenPath = await CreateOwnershipTokenFileAsync(game);

            var (exchangeCode, userData) = credentials.Value;
            var started = TryStartGame(appName, () => StartGame(gameInfo, game, exchangeCode, userData, ownershipTokenPath));
            if (!started) DeleteOwnershipToken(ownershipTokenPath);
            return started;
        }
        catch (Exception ex)
        {
            DeleteOwnershipToken(ownershipTokenPath);
            _log.Error(ex, "LaunchApp failed");
            return false;
        }
    }

    internal void TrackOperation(OperationSnapshot operation)
    {
        lock (_operationLock)
        {
            if (operation.IsActive)
            {
                if (!_activeOperationApps.ContainsKey(operation.AppName))
                    _activeOperationApps[operation.AppName] = operation.OperationId;
            }
            else if (_activeOperationApps.TryGetValue(operation.AppName, out var activeId) && activeId == operation.OperationId)
                _activeOperationApps.Remove(operation.AppName);
        }
    }

    internal bool TryQueueOperation(OperationSnapshot operation, Action enqueue, bool excludeRelatedOperations = false)
    {
        lock (_operationLock)
        {
            if (_activeOperationApps.ContainsKey(operation.AppName) ||
                (excludeRelatedOperations && HasRelatedOperation(operation.AppName))) return false;
            _activeOperationApps[operation.AppName] = operation.OperationId;
            try
            {
                enqueue();
                return true;
            }
            catch
            {
                _activeOperationApps.Remove(operation.AppName);
                throw;
            }
        }
    }

    internal bool TryStartGame(string appName, Func<bool> start)
    {
        lock (_operationLock)
        {
            if (HasRelatedOperation(appName) || !CanLaunch(_storage.GetGameMetaData(appName))) return false;
            return start();
        }
    }

    private bool HasRelatedOperation(string appName)
    {
        var baseAppName = GetBaseGameAppName(appName);
        return _activeOperationApps.Keys.Any(operation => IsOperationForGame(operation, baseAppName));
    }

    private (LocalAppState State, Game Game)? GetLaunchContext(string appName)
    {
        lock (_operationLock)
        {
            if (string.IsNullOrWhiteSpace(appName) || HasRelatedOperation(appName) ||
                !_storage.LocalAppStateDictionary.TryGetValue(appName, out var gameInfo))
                return null;
            _log.Information("LaunchApp: Trying to launch app: {AppName}", appName);
            var game = _storage.GetGameMetaData(appName);
            return CanLaunch(game) ? (gameInfo, game!) : null;
        }
    }

    private async Task<(string ExchangeCode, UserData User)?> GetLaunchCredentialsAsync(string appName)
    {
        var responseData = await _storeRepository.GetGameToken();
        var userData = await _authManager.GetUserData();
        var gameToken = string.IsNullOrWhiteSpace(responseData)
            ? null
            : JsonSerializer.Deserialize<GameTokenResponse>(responseData);
        if (gameToken is not null && !string.IsNullOrWhiteSpace(gameToken.Code) && userData is not null)
            return (gameToken.Code, userData);

        _log.Error("LaunchApp: Failed to obtain launch credentials for {AppName}", appName);
        return null;
    }

    private bool StartGame(
        LocalAppState gameInfo,
        Game game,
        string exchangeCode,
        UserData userData,
        string? ownershipTokenPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = InstallPathPolicy.ResolveFile(gameInfo.InstallPath, gameInfo.Executable),
            Arguments = BuildLaunchArguments(gameInfo, game, exchangeCode, userData, ownershipTokenPath),
            UseShellExecute = false,
            WorkingDirectory = gameInfo.InstallPath
        };

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            DeleteOwnershipToken(ownershipTokenPath);
            return false;
        }

        if (ownershipTokenPath is null)
            process.Dispose();
        else
            _ = DeleteOwnershipTokenAfterExitAsync(process, ownershipTokenPath);
        return true;
    }

    private bool CanLaunch(Game? game)
    {
        if (game is null)
        {
            _log.Warning("LaunchApp: Trying to launch a game that is not owned");
            return false;
        }

        if (game.LocalAppState?.InstallStatus is not InstallState.Installed and not InstallState.NeedUpdate)
        {
            _log.Warning("LaunchApp: Trying to launch a game that is not installed");
            return false;
        }

        if (game.IsDlc())
        {
            _log.Warning("LaunchApp: Launching DLC is not yet supported");
            return false;
        }

        return true;
    }

    private async Task<string> CreateOwnershipTokenFileAsync(Game game)
    {
        var token = await _storeRepository.GetOwnershipToken(
            game.AssetInfos.Windows.Namespace,
            game.AssetInfos.Windows.CatalogItemId);
        if (token is null || token.Length == 0)
            throw new InvalidOperationException("The ownership token could not be obtained.");

        var directory = Path.Combine(Path.GetTempPath(), "Crimson", "ownership-tokens");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.ovt");
        await File.WriteAllBytesAsync(path, token);
        return path;
    }

    internal static string BuildLaunchArguments(
        LocalAppState gameInfo,
        Game game,
        string exchangeCode,
        UserData userData,
        string? ownershipTokenPath)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameInfo.LaunchParameters))
            parameters.Add(gameInfo.LaunchParameters);
        var additionalCommandLine = game.Metadata.CustomAttributes?.AdditionalCommandLine?.Value;
        if (!string.IsNullOrWhiteSpace(additionalCommandLine))
            parameters.Add(additionalCommandLine);

        parameters.Add("-AUTH_LOGIN=unused");
        parameters.Add($"-AUTH_PASSWORD={exchangeCode}");
        parameters.Add("-AUTH_TYPE=exchangecode");
        parameters.Add($"-epicapp={gameInfo.AppName}");
        parameters.Add("-epicenv=Prod");
        if (!string.IsNullOrWhiteSpace(ownershipTokenPath))
            parameters.Add($"-epicovt=\"{ownershipTokenPath}\"");
        parameters.Add("-EpicPortal");
        parameters.Add($"-epicusername=\"{userData.DisplayName}\"");
        parameters.Add($"-epicuserid={userData.AccountId}");
        parameters.Add("-epiclocale=en");
        parameters.Add($"-epicsandboxid={game.AssetInfos.Windows.Namespace}");
        return string.Join(" ", parameters);
    }

    private async Task DeleteOwnershipTokenAfterExitAsync(Process process, string path)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed while waiting to remove an ownership token");
        }
        finally
        {
            process.Dispose();
            DeleteOwnershipToken(path);
        }
    }

    private void DeleteOwnershipToken(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to remove an ownership token file");
        }
    }

    /// <summary>
    ///  Updates library data and triggers LibraryUpdated event
    /// </summary>
    private async Task UpdateLibraryData(int generation, int revision, bool forceMetadataUpdate)
    {
        try
        {
            var gameAssets = await GetGameAssetsAsync();
            if (gameAssets is null)
                return;

            List<FetchListItem> fetchList;
            lock (_storage.AccountDataLock)
            {
                if (generation != _storage.AccountGeneration)
                    return;
                fetchList = GetMetadataFetchList(gameAssets, forceMetadataUpdate);
            }

            var fetchedGames = await FetchMetadataAsync(fetchList, gameAssets);
            PublishLibrary(generation, revision, gameAssets, fetchedGames);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "UpdateLibraryData failed");
        }
    }

    private void PublishLibrary(int generation, int revision, List<Asset> assets, IReadOnlyList<Game> fetchedGames)
    {
        lock (_storage.AccountDataLock)
        {
            if (generation != _storage.AccountGeneration)
                return;

            foreach (var game in fetchedGames)
                _storage.SaveMetaData(game);
            _storage.SaveGameAssets(assets);
            _storage.HydrateAllLocalAppStates();
            CheckForGameUpdates(assets);

            var ownedGames = SelectOwnedGames(assets, _storage.GameMetaDataDictionary);
            _ownedAppNames = ownedGames.Select(game => game.AppName).ToHashSet(StringComparer.Ordinal);
            _ownedAccountGeneration = generation;
            var complete = GetMetadataFetchList(assets, false).Count == 0;
            _lastUpdateDateTime = complete && revision == _cacheRevision
                ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
            _storage.SaveLibraryCache(assets, _lastUpdateDateTime);
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(ownedGames);
        }
    }

    private async Task<List<Asset>?> GetGameAssetsAsync()
    {
        _log.Information("UpdateLibraryData: Refreshing game assets");
        var assets = (await _storeRepository.FetchGameAssets())?.ToList();
        if (assets is null)
        {
            _log.Error("GetLibraryData: Error while fetching game assets");
            return null;
        }

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
                (asset.BuildVersion != game.AssetInfos?.Windows?.BuildVersion ||
                 asset.CatalogItemId != game.AssetInfos?.Windows?.CatalogItemId ||
                 asset.Namespace != game.AssetInfos?.Windows?.Namespace);
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

    private async Task<IReadOnlyList<Game>> FetchMetadataAsync(
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

        return fetchedGames.ToList();
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

internal sealed record StoreProductCacheEntry(
    StoreProductDetails? Details,
    DateTimeOffset ExpiresAt);

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
