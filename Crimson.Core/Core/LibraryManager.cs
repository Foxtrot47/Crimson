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

/// <summary>
/// Owns the account's library and its caches. Ownership is persisted per account in
/// <c>library-cache.json</c> and reused for 20 minutes; an expired snapshot is still
/// shown when a refresh fails. Store product details are public, shared across
/// accounts, and kept for 24 hours in <c>store-details.json</c>. Playtime is never
/// cached. Logout clears ownership and metadata but keeps installation records.
/// </summary>
public class LibraryManager
{
    private readonly ILogger _log;
    private readonly IStoreRepository _storeRepository;
    private readonly Storage _storage;
    private readonly AuthManager _authManager;

    internal int AccountGeneration => _storage.AccountGeneration;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    /// <summary>
    /// Raised once per newly owned base game, after its metadata is available. The first
    /// load for an account, including a restored snapshot, is the baseline and is silent.
    /// </summary>
    public event Action<IReadOnlyList<Game>>? GamesAdded;
    public event Action<string, StoreProductDetails>? StoreProductDetailsUpdated;

    private DateTimeOffset _lastUpdateDateTime = DateTimeOffset.MinValue;
    private int _cacheRevision;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private IReadOnlySet<string> _ownedAppNames = new HashSet<string>();
    private readonly ConcurrentDictionary<string, StoreProductCacheEntry> _storeProductCache = new();
    private readonly Dictionary<string, long> _storeProductFetchSequences = new();
    private readonly object _storeProductCacheLock = new();
    private int _ownedAccountGeneration = -1;
    private bool _emptyOwnershipHeld;
    private readonly object _operationLock = new();
    private readonly Dictionary<string, Guid> _activeOperationApps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingAddedAppNames = new(StringComparer.Ordinal);
    private volatile bool _authenticationCheckInProgress;

    public bool IsAuthenticationCheckInProgress => _authenticationCheckInProgress;

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
        _authenticationCheckInProgress = e.NewStatus == AuthenticationStatus.Checking;
        if (e.NewStatus != AuthenticationStatus.LoggedOut)
            return;

        lock (_storage.AccountDataLock)
        {
            _ownedAppNames = new HashSet<string>();
            _pendingAddedAppNames.Clear();
            _ownedAccountGeneration = -1;
            _emptyOwnershipHeld = false;
            _lastUpdateDateTime = DateTimeOffset.MinValue;
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
        }
    }

    public async Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default)
    {
        int accountGeneration;
        lock (_storage.AccountDataLock)
        {
            if (_ownedAccountGeneration != _storage.AccountGeneration || !_ownedAppNames.Contains(appName))
                return null;
            accountGeneration = _storage.AccountGeneration;
        }

        var seconds = await _storeRepository.FetchGamePlaytimeSeconds(appName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!seconds.HasValue) return null;

        lock (_storage.AccountDataLock)
        {
            if (accountGeneration != _storage.AccountGeneration || !_ownedAppNames.Contains(appName))
                return null;
        }

        return seconds;
    }

    public void ClearStoreProductDetailsCache()
    {
        lock (_storeProductCacheLock)
        {
            _storeProductCache.Clear();
            _storage.ClearStoreDetailsCache();
        }
    }

    public async Task<StoreProductDetails?> GetStoreProductDetails(
        string nameSpace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameSpace)) return null;

        var cached = GetCachedStoreProductDetails(nameSpace);
        if (cached is null)
            return await FetchStoreProductDetailsAsync(nameSpace, cancellationToken);

        // The refresh outlives the caller, so leaving the page must not cancel it.
        if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
            _ = RefreshStoreProductDetailsInBackgroundAsync(nameSpace);
        return cached.Details;
    }

    private StoreProductCacheEntry? GetCachedStoreProductDetails(string nameSpace)
    {
        lock (_storeProductCacheLock)
        {
            if (_storeProductCache.TryGetValue(nameSpace, out var cached)) return cached;
            var persisted = _storage.ReadStoreProductDetailsCache(nameSpace);
            if (persisted is not { } entry) return null;
            cached = new StoreProductCacheEntry(entry.Details, entry.SavedAt.AddHours(24));
            _storeProductCache[nameSpace] = cached;
            return cached;
        }
    }

    private async Task<StoreProductDetails?> FetchStoreProductDetailsAsync(
        string nameSpace,
        CancellationToken cancellationToken)
    {
        long fetchSequence;
        lock (_storeProductCacheLock)
        {
            _storeProductFetchSequences.TryGetValue(nameSpace, out fetchSequence);
            _storeProductFetchSequences[nameSpace] = ++fetchSequence;
        }

        var details = await _storeRepository.FetchStoreProductDetails(nameSpace, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (details is null) return null;

        var savedAt = DateTimeOffset.UtcNow;
        var accepted = false;
        lock (_storeProductCacheLock)
        {
            if (_storeProductFetchSequences.GetValueOrDefault(nameSpace) == fetchSequence)
            {
                _storeProductCache[nameSpace] = new StoreProductCacheEntry(details, savedAt.AddHours(24));
                accepted = true;
            }
        }

        if (accepted)
        {
            _storage.SaveStoreProductDetailsCache(nameSpace, details, savedAt);
            StoreProductDetailsUpdated?.Invoke(nameSpace, details);
        }
        return details;
    }

    private async Task RefreshStoreProductDetailsInBackgroundAsync(string nameSpace)
    {
        try
        {
            await FetchStoreProductDetailsAsync(nameSpace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to refresh cached Store product details");
        }
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
                if (_authenticationCheckInProgress)
                    return GetOwnedGames();
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
                if (ShouldHoldEmptyOwnership(generation, gameAssets))
                {
                    _log.Warning("UpdateLibraryData: Ignoring an empty ownership response until it is confirmed");
                    return;
                }
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

    // An empty response after a non-empty library may be a degraded Epic reply. Keep the
    // current library and cache until a second consecutive empty response confirms it.
    private bool ShouldHoldEmptyOwnership(int generation, List<Asset> assets)
    {
        if (assets.Count > 0 || _ownedAccountGeneration != generation || _ownedAppNames.Count == 0)
        {
            _emptyOwnershipHeld = false;
            return false;
        }
        _emptyOwnershipHeld = !_emptyOwnershipHeld;
        return _emptyOwnershipHeld;
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
            var ownedAppNames = assets.Select(asset => asset.AppName).ToHashSet(StringComparer.Ordinal);
            if (_ownedAccountGeneration == generation)
                _pendingAddedAppNames.UnionWith(ownedAppNames.Except(_ownedAppNames));
            else
                _pendingAddedAppNames.Clear(); // First load is a baseline, not a list of new purchases.
            _pendingAddedAppNames.IntersectWith(ownedAppNames);
            var addedGames = ownedGames
                .Where(game => _pendingAddedAppNames.Contains(game.AppName) && !game.IsDlc())
                .OrderBy(game => game.AppTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // Keep additions with unavailable metadata pending until they can be displayed.
            _pendingAddedAppNames.ExceptWith(ownedGames.Select(game => game.AppName));
            _ownedAppNames = ownedAppNames;
            _ownedAccountGeneration = generation;
            var complete = GetMetadataFetchList(assets, false).Count == 0;
            _lastUpdateDateTime = complete && revision == _cacheRevision
                ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
            _storage.SaveLibraryCache(assets, _lastUpdateDateTime);
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(ownedGames);
            if (generation == _storage.AccountGeneration && addedGames.Count > 0)
                GamesAdded?.Invoke(addedGames);
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
