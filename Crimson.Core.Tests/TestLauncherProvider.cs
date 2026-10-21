using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;

namespace Crimson.Tests;

// Deliberately uses no Epic protocol models, repositories, manifests, or token shape.
internal sealed class TestLauncherProvider : ILauncherProvider
{
    public string Id { get; }
    public string DisplayName => Id;
    public TestAuthentication Auth { get; } = new();
    public TestLibrary Games { get; }
    public TestInstaller Jobs { get; }
    public ILauncherAuthentication Authentication => Auth;
    public ILauncherLibrary Library => Games;
    public ILauncherInstaller? Installer { get; set; }
    public ILauncherStore? Store { get; set; }

    public TestLauncherProvider(string id)
    {
        Id = id;
        Games = new TestLibrary(id);
        Jobs = new TestInstaller(id);
        Installer = Jobs;
    }

    internal sealed class TestAuthentication : ILauncherAuthentication
    {
        public AuthenticationStatus AuthenticationStatus { get; private set; } = AuthenticationStatus.LoggedIn;
        public event Action<AuthenticationStatus>? StatusChanged;
        public Task<AuthenticationStatus> CheckAuthStatus() => Task.FromResult(AuthenticationStatus);
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

    internal sealed class TestLibrary(string providerId) : ILauncherLibrary
    {
        public List<LauncherGame> Games { get; } = [];
        public TaskCompletionSource<IEnumerable<LauncherGame>>? Pending { get; set; }
        public Exception? Failure { get; set; }
        public List<string> Launched { get; } = [];
        public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
        public event Action<LauncherGame>? GameStatusUpdated;
        public LauncherGame Add(string localId, InstallState state = InstallState.Installed, bool canLaunch = true)
        {
            var game = new LauncherGame
            {
                Id = new GameId(providerId, localId), AppTitle = "A game from " + providerId,
                CoverImageUrl = "https://images.example.test/cover.png", CanLaunch = canLaunch,
                LocalAppState = new LauncherInstallation(state, Path.GetTempPath())
            };
            Games.Add(game);
            return game;
        }
        public Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false) =>
            Failure is not null ? Task.FromException<IEnumerable<LauncherGame>>(Failure) : Pending?.Task ?? Task.FromResult<IEnumerable<LauncherGame>>(Games.ToList());
        public LauncherGame? GetGameInfo(string appName) => Games.SingleOrDefault(game => game.AppName == appName);
        public List<LauncherGame> GetDlcsForGame(string appName) => [];
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
        public List<InstallItem> Queued { get; } = [];
        public List<string> Cancelled { get; } = [];
        public int Pauses { get; private set; }
        public int Loads { get; private set; }
        public event Action<InstallItem>? InstallationStatusChanged;
        public event Action<InstallItem>? InstallProgressUpdate;
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
