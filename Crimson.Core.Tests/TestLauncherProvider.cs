using Crimson.Core;
using Crimson.Repository;
using Crimson.Providers.Epic;
using Crimson.Utils;
using Serilog;
using Crimson.Models;
using Crimson.Providers;

namespace Crimson.Tests;

// Deliberately uses no Epic protocol models, repositories, manifests, or token shape.
internal sealed class TestLauncherProvider : ILauncherProvider
{
    public string Id { get; }
    public string DisplayName => Id;
    public TestAuthentication Auth { get; }
    public TestLibrary Games { get; }
    public TestInstaller Jobs { get; }
    public ILauncherAuthentication Authentication => Auth;
    public ILauncherLibrary Library => Games;
    public ILauncherInstaller? Installer { get; set; }
    public ILauncherStore? Store { get; set; }

    public TestLauncherProvider(string id)
    {
        Id = id;
        Auth = new TestAuthentication(id);
        Games = new TestLibrary(id);
        Jobs = new TestInstaller(id);
        Installer = Jobs;
    }

    internal sealed class TestAuthentication(string providerId) : ILauncherAuthentication
    {
        private string ProviderId() => providerId;
        public AuthenticationStatus AuthenticationStatus { get; private set; } = AuthenticationStatus.LoggedIn;
        public event Action<AuthenticationStatus>? StatusChanged;
        public Task<AuthenticationStatus> CheckAuthStatus() => Task.FromResult(AuthenticationStatus);
        public Task<LauncherUser?> GetUser(CancellationToken cancellationToken = default) =>
            Task.FromResult<LauncherUser?>(new LauncherUser(ProviderId() + " account", ProviderId() + "-user"));
        public void SignIn()
        {
            AuthenticationStatus = AuthenticationStatus.LoggedIn;
            StatusChanged?.Invoke(AuthenticationStatus);
        }
        public Task Logout()
        {
            AuthenticationStatus = AuthenticationStatus.LoggedOut;
            StatusChanged?.Invoke(AuthenticationStatus);
            return Task.CompletedTask;
        }
    }

    internal sealed class TestLibrary(string providerId) : ILauncherLibrary, IGameDetailsSource, IGameCloudSaves
    {
        public Uri? StorePageUri { get; set; }
        public long? PlaytimeSeconds { get; set; }
        public bool Running { get; set; }
        public event Action<string, bool>? RunningStateChanged;
        public event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;
        public bool IsRunning(string appName) => Running;
        public bool AllowsMultipleInstances(string appName) => false;
        public LauncherGameDetails? GetGameDetails(string appName) => new(
            CoverImageUrl: Games.FirstOrDefault(game => game.AppName == appName)?.CoverImageUrl,
            LandscapeImageUrl: Games.FirstOrDefault(game => game.AppName == appName)?.LandscapeImageUrl,
            DeveloperName: "Test developer", Description: "Test description");
        public Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) =>
            Task.FromResult(PlaytimeSeconds);
        public Task<StoreProductDetails?> GetStoreProductDetails(string appName, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoreProductDetails?>(null);
        public Uri? GetStorePageUri(string appName) => StorePageUri;
        public bool Supports(string appName) => false;
        public CloudSaveSyncStatus GetSyncStatus(string appName) => CloudSaveSyncStatus.Idle;
        public void PublishRunning(bool running) { Running = running; RunningStateChanged?.Invoke(Games[0].AppName, running); }

        public List<LauncherGame> Games { get; } = [];
        public TaskCompletionSource<IEnumerable<LauncherGame>>? Pending { get; set; }
        public Exception? Failure { get; set; }
        public List<string> Launched { get; } = [];
        public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
        public event Action<LauncherGame>? GameStatusUpdated;
        public LauncherGame Add(string localId, InstallState state = InstallState.Installed, bool canLaunch = true,
            string? coverImageUrl = "https://images.example.test/cover.png")
        {
            var game = new LauncherGame
            {
                Id = new GameId(providerId, localId), AppTitle = "A game from " + providerId,
                CoverImageUrl = coverImageUrl, CanLaunch = canLaunch,
                LocalAppState = new LauncherInstallation(state, Path.GetTempPath())
            };
            Games.Add(game);
            return game;
        }
        public Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false) =>
            Failure is not null ? Task.FromException<IEnumerable<LauncherGame>>(Failure) : Pending?.Task ?? Task.FromResult<IEnumerable<LauncherGame>>(Games.ToList());
        public LauncherGame? GetGameInfo(string appName) => Games.SingleOrDefault(game => game.AppName == appName);
        public List<LauncherGame> GetDlcsForGame(string appName) => [];
        public string GetBaseGameAppName(string appName) => appName;
        public Task<bool> LaunchApp(string appName)
        {
            Launched.Add(appName);
            return Task.FromResult(true);
        }
        public void InvalidateCache() { }
        public void Publish() => LibraryUpdated?.Invoke(Games);
        public void PublishStatus(LauncherGame game) => GameStatusUpdated?.Invoke(game);
    }

    internal sealed class TestInstaller(string providerId) : ILauncherInstaller
    {
        public HashSet<ActionType> Actions { get; } = [ActionType.Install];
        public bool Supports(ActionType action) => Actions.Contains(action);
        public bool SupportsPause { get; set; } = true;
        public bool SupportsShortcuts => false;
        public InstallItem? CurrentInstall { get; private set; }
        public OperationSnapshot? CurrentOperation { get; private set; }
        public IReadOnlyList<OperationSnapshot> GetQueueOperations() => CurrentOperation is null ? [] : [CurrentOperation];
        public IReadOnlyList<OperationSnapshot> GetHistoryOperations() => [];
        public bool RetryFailedOperation(string appName)
        {
            Retried.Add(appName);
            return true;
        }
        public List<string> Retried { get; } = [];
        public List<InstallItem> Queued { get; } = [];
        public List<string> Cancelled { get; } = [];
        public int Pauses { get; private set; }
        public int Loads { get; private set; }
        public event Action<InstallItem>? InstallationStatusChanged;
        public event Action<InstallItem>? InstallProgressUpdate;
        public event Action<OperationSnapshot>? OperationUpdated;
        public event Action<OperationSnapshot>? TransferRatesUpdated;
        public event Action<OperationSnapshot>? OperationError;
        public OperationSnapshot? GetActiveOperationForGame(string appName) =>
            CurrentOperation?.AppName == appName ? CurrentOperation : null;
        public bool TryAddToQueue(InstallItem item) { AddToQueue(item); return true; }
        public List<(string AppName, ActionType Action, OperationFailure Failure)> Failures { get; } = [];
        public void ReportPreparationFailure(string appName, ActionType action, OperationFailure failure) =>
            Failures.Add((appName, action, failure));
        public void ReportPreparationFailure(string appName, ActionType action, Exception exception) =>
            Failures.Add((appName, action, OperationFailureClassifier.Classify(exception)));
        public void ReportSecondaryFailure(InstallItem install, OperationFailure failure) =>
            Failures.Add((install.AppName, install.Action, failure));
        public void AddToQueue(InstallItem item)
        {
            Assert.Equal(providerId, GameId.Parse(item.AppName).ProviderId);
            Queued.Add(item);
        }
        public void Start()
        {
            CurrentInstall = Queued[0];
            CurrentInstall.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);
        }
        public void Progress() => InstallProgressUpdate?.Invoke(CurrentInstall!);
        public List<string> GetQueueItemNames() => Queued.Select(item => item.AppName).ToList();
        public List<string> GetHistoryItemsNames() => [];
        public void CancelInstall(string appName) => Cancelled.Add(appName);
        public void PauseInstall() => Pauses++;
        public void ResumeInstall() { }
        public Task StopProcessing() => Task.CompletedTask;
        public Task LoadPendingInstalls() { Loads++; return Task.CompletedTask; }
        public Task<(double DownloadBytes, double InstallBytes)> GetGameDownloadInstallSizes(
            string appName, CancellationToken cancellationToken = default) => Task.FromResult((100d, 200d));
    }
}

/// <summary>Signs in without touching the network so provider aggregation can be exercised.</summary>
internal sealed class AlwaysSignedInAuthentication : ILauncherAuthentication
{
    public AuthenticationStatus AuthenticationStatus { get; private set; } = AuthenticationStatus.LoggedIn;
    public event Action<AuthenticationStatus>? StatusChanged;
    public Task<AuthenticationStatus> CheckAuthStatus() => Task.FromResult(AuthenticationStatus);
    public Task<LauncherUser?> GetUser(CancellationToken cancellationToken = default) =>
        Task.FromResult<LauncherUser?>(new LauncherUser("Epic Account", "epic-user"));
    public void SignOut()
    {
        AuthenticationStatus = AuthenticationStatus.LoggedOut;
        StatusChanged?.Invoke(AuthenticationStatus);
    }

    public Task Logout()
    {
        SignOut();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hosts the real Epic adapters behind the shared launcher services so presentation tests
/// exercise the same path the application uses.
/// </summary>
internal sealed record EpicHost(LauncherRegistry Registry, LauncherLibrary Library, LauncherInstaller Installer,
    AlwaysSignedInAuthentication Auth)
{
    public static EpicHost Create(EpicLibraryManager library, EpicInstallManager? installer,
        IEpicGamesRepository repository, CloudSaveManager? cloudSaves = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-epic-host-" + Guid.NewGuid().ToString("N"));
        var auth = new EpicAuthManager(new LoggerConfiguration().CreateLogger(),
            new Storage(new LoggerConfiguration().CreateLogger(), root, Path.Combine(root, "games")),
            new TestCredentialProtector(), new HttpClient());
        var account = new AlwaysSignedInAuthentication();
        var provider = new EpicLauncherProvider(auth, library, installer, repository, cloudSaves, account);
        var registry = new LauncherRegistry([provider]);
        return new EpicHost(registry, new LauncherLibrary(registry), new LauncherInstaller(registry), account);
    }
}
