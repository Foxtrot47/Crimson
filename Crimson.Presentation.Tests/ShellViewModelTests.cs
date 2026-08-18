using Crimson.Core;
using Crimson.Models;
using Crimson.Presentation;
using Xunit;

namespace Crimson.Presentation.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task SuccessfulLoginRoutesToLibrary()
    {
        var navigation = new NavigationService();
        var authentication = new StubAuthenticationService();
        var login = new LoginViewModel(authentication, navigation);
        var dispatcher = new ImmediateUiDispatcher();
        var workflow = new StubGameWorkflowService();
        var library = new LibraryViewModel(new StubLibraryService(), dispatcher, navigation);
        var folderPicker = new StubFolderPicker();
        var gameInfo = new GameInfoViewModel(
            workflow,
            new StubInstallDialog(),
            folderPicker,
            dispatcher);
        var currentOperation = new CurrentOperationViewModel(workflow, dispatcher);
        var downloads = new DownloadsViewModel(workflow, dispatcher, currentOperation);
        var settings = new SettingsViewModel(
            new StubSettingsService(),
            folderPicker,
            new StubPathLauncher(),
            "logs");
        var shell = new ShellViewModel(
            navigation,
            dispatcher,
            login,
            library,
            gameInfo,
            downloads,
            settings);
        await shell.ActivateAsync();

        await login.AcceptExchangeCodeAsync("valid-code");

        Assert.Same(library, shell.CurrentPage);
        Assert.IsType<LibraryRoute>(navigation.Current);
        shell.Dispose();
        Assert.Equal(0, authentication.SubscriberCount);
    }

    private sealed class StubAuthenticationService : IEpicAuthenticationService
    {
        public EpicAuthenticationSnapshot Snapshot { get; private set; } =
            new(EpicAuthenticationState.LoggedOut);

        public int SubscriberCount { get; private set; }

        private EventHandler<EpicAuthenticationSnapshot>? _changed;

        public event EventHandler<EpicAuthenticationSnapshot>? Changed
        {
            add { _changed += value; SubscriberCount++; }
            remove { _changed -= value; SubscriberCount--; }
        }

        public Task<EpicAuthenticationSnapshot> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<EpicAuthenticationSnapshot> LoginWithExchangeCodeAsync(
            string exchangeCode,
            CancellationToken cancellationToken = default) => LoginAsync();

        public Task<string?> GetAccessToken(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("token");

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedOut);
            _changed?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        private Task<EpicAuthenticationSnapshot> LoginAsync()
        {
            Snapshot = new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedIn, "Player");
            _changed?.Invoke(this, Snapshot);
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class StubLibraryService : ILibraryService
    {
        public LibrarySnapshot Snapshot { get; } = LibrarySnapshot.Empty;

        public event EventHandler<LibrarySnapshot>? Changed
        {
            add { }
            remove { }
        }

        public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LibraryRefreshResult> RefreshAsync(
            bool force = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LibraryRefreshResult(Snapshot));
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings("games"));

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(
            string? suggestedPath,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class StubPathLauncher : IExternalPathLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubInstallDialog : IInstallDialogService
    {
        public Task ShowAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Close()
        {
        }
    }

    private sealed class StubGameWorkflowService : IGameWorkflowService
    {
        public event EventHandler<GamePresentationData>? GameChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<InstallOperationSnapshot?>? OperationChanged
        {
            add { }
            remove { }
        }
        public InstallOperationSnapshot? CurrentOperation => null;
        public GamePresentationData? GetGame(string appName) => null;
        public IReadOnlyList<GamePresentationData> GetDlcs(string appName) => [];
        public IReadOnlyList<GamePresentationData> GetQueuedGames() => [];
        public IReadOnlyList<GamePresentationData> GetHistoryGames() => [];
        public Task LaunchAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<InstallCommandResult> EnqueueAsync(
            InstallOperationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallCommandResult(InstallCommandOutcome.Accepted));
        public Task<InstallCommandResult> CancelAsync(
            string appName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallCommandResult(InstallCommandOutcome.Accepted));
        public Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallCommandResult(InstallCommandOutcome.Accepted));
        public Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallCommandResult(InstallCommandOutcome.Accepted));
        public Task<InstallContentSize> GetContentSizeAsync(
            string appName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallContentSize());
        public Task<DriveSpaceSnapshot?> GetDriveSpaceAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DriveSpaceSnapshot?>(null);
    }
}
