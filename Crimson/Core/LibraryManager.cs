using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly IGameProcessRunner _processRunner;
    private readonly ILibraryService _libraryService;
    private readonly ILaunchPlanner _launchPlanner;
    private readonly IRuntimeProfileResolver _runtimeProfileResolver;
    private readonly IInstallRecoveryStatus _recoveryStatus;

    public event Action<IEnumerable<Game>> LibraryUpdated;
    public event Action<Game> GameStatusUpdated;

    public LibraryManager(
        ILogger log,
        IStoreRepository repository,
        Storage storage,
        AuthManager authManager,
        IGameProcessRunner processRunner,
        ILibraryService libraryService,
        ILaunchPlanner launchPlanner,
        IRuntimeProfileResolver runtimeProfileResolver,
        IInstallRecoveryStatus recoveryStatus)
    {
        _log = log;
        _storeRepository = repository;
        _storage = storage;
        _authManager = authManager;
        _processRunner = processRunner;
        _libraryService = libraryService;
        _launchPlanner = launchPlanner;
        _runtimeProfileResolver = runtimeProfileResolver;
        _recoveryStatus = recoveryStatus;
    }

    public async Task<IEnumerable<Game>> GetLibraryData(bool forceUpdate = false)
    {
        ReconcileMissingInstallations();
        var result = await _libraryService.RefreshAsync(forceUpdate);
        if (!result.IsSuccess)
        {
            _log.Warning(
                "Library refresh failed with {FailureKind}: {Message}",
                result.Failure!.Kind,
                result.Failure.Message);
        }
        _storage.HydrateAllLocalAppStates();
        var games = _storage.GameMetaDataDictionary.Values.ToList();
        if (result.IsSuccess)
            LibraryUpdated?.Invoke(games);
        return games;
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

                if (_recoveryStatus.HasUnresolvedTransaction(gameInfo.InstallPath))
                {
                    _log.Warning("LaunchApp: install recovery is unresolved for {AppName}", appName);
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

                var snapshot = new GameSnapshot(
                    metaData.AppName,
                    metaData.AppTitle,
                    null,
                    metaData.AssetInfos.Windows.Namespace,
                    metaData.AssetInfos.Windows.CatalogItemId,
                    metaData.AssetInfos.Windows.BuildVersion,
                    gameInfo.AvailableManifestDigest,
                    gameInfo.InstalledManifestBuildVersion ?? gameInfo.Version,
                    gameInfo.InstalledManifestSha1,
                    gameInfo.InstalledManifestSha256,
                    gameInfo.InstallStatus,
                    gameInfo.InstallStatus == InstallState.NeedUpdate
                        ? GameUpdateClassification.UpdateAvailable
                        : GameUpdateClassification.Current,
                    gameInfo.InstallPath,
                    gameInfo.Executable);
                var runtimeProfile = await _runtimeProfileResolver.ResolveAsync(snapshot);
                var plan = _launchPlanner.Create(
                    snapshot,
                    new LaunchCredentials(responseObject.Code, userData.AccountId, userData.DisplayName),
                    runtimeProfile);
                await _processRunner.RunAsync(plan);
            }
        }
        catch (Exception ex)
        {
            _log.Fatal("LaunchApp: Exception: {@ex}", ex);
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
}

public class GameTokenResponse
{
    [JsonPropertyName("expiresInSeconds")] public int ExpiresInSeconds { get; set; }

    [JsonPropertyName("code")] public string Code { get; set; }

    [JsonPropertyName("creatingClientId")] public string CreatingClientId { get; set; }
}
