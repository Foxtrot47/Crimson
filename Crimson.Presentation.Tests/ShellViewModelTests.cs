using Crimson.Core;
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
        var library = new LibraryViewModel(
            new StubLibraryService(),
            new ImmediateUiDispatcher(),
            navigation);
        var settings = new SettingsViewModel(
            new StubSettingsService(),
            new StubFolderPicker(),
            new StubPathLauncher(),
            "logs");
        using var shell = new ShellViewModel(
            navigation,
            new ImmediateUiDispatcher(),
            login,
            library,
            settings);
        await shell.ActivateAsync();

        await login.AcceptExchangeCodeAsync("valid-code");

        Assert.Same(library, shell.CurrentPage);
        Assert.IsType<LibraryRoute>(navigation.Current);
    }

    private sealed class StubAuthenticationService : IEpicAuthenticationService
    {
        public EpicAuthenticationSnapshot Snapshot { get; private set; } =
            new(EpicAuthenticationState.LoggedOut);

        public event EventHandler<EpicAuthenticationSnapshot>? Changed;

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
            Changed?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        private Task<EpicAuthenticationSnapshot> LoginAsync()
        {
            Snapshot = new EpicAuthenticationSnapshot(EpicAuthenticationState.LoggedIn, "Player");
            Changed?.Invoke(this, Snapshot);
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
}
