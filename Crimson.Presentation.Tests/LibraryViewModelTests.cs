using Crimson.Core;
using Crimson.Models;
using Crimson.Presentation;
using Xunit;

namespace Crimson.Presentation.Tests;

public sealed class LibraryViewModelTests
{
    [Fact]
    public async Task ActivationLoadsSnapshotAndOwnsSubscription()
    {
        var service = new StubLibraryService(Snapshot(1, Game("game", "Game", "1.0", true)));
        var navigation = new NavigationService();
        var viewModel = new LibraryViewModel(service, new ImmediateUiDispatcher(), navigation);

        await viewModel.ActivateAsync();
        await viewModel.ActivateAsync();

        Assert.Equal(1, service.RefreshCalls);
        Assert.Equal(1, service.SubscriberCount);
        var game = Assert.Single(viewModel.Games);
        Assert.Equal("Game", game.Title);
        Assert.True(viewModel.HasGames);

        viewModel.OpenGameCommand.Execute(game);
        Assert.Equal(new GameRoute("game"), navigation.Current);

        viewModel.Deactivate();
        Assert.Equal(0, service.SubscriberCount);
    }

    [Fact]
    public async Task ChangedSnapshotUpdatesActiveViewOnly()
    {
        var service = new StubLibraryService(Snapshot(1));
        var viewModel = new LibraryViewModel(
            service,
            new ImmediateUiDispatcher(),
            new NavigationService());
        await viewModel.ActivateAsync();

        service.Publish(Snapshot(2, Game("new", "New Game", "2.0", false)));
        Assert.Equal("New Game", Assert.Single(viewModel.Games).Title);

        viewModel.Deactivate();
        service.Publish(Snapshot(3));
        Assert.Single(viewModel.Games);
    }

    private static LibrarySnapshot Snapshot(long sequence, params GameSnapshot[] games) =>
        new(sequence, DateTimeOffset.UtcNow, [.. games]);

    private static GameSnapshot Game(
        string appName,
        string title,
        string assetBuildVersion,
        bool installed) => new(
        appName,
        title,
        new Uri("https://example.test/game.png"),
        "namespace",
        "catalog",
        assetBuildVersion,
        null,
        installed ? assetBuildVersion : null,
        null,
        null,
        installed ? InstallState.Installed : InstallState.NotInstalled,
        installed ? GameUpdateClassification.Current : GameUpdateClassification.NotInstalled,
        installed ? "games" : null,
        installed ? "game.exe" : null);

    private sealed class StubLibraryService(LibrarySnapshot snapshot) : ILibraryService
    {
        private EventHandler<LibrarySnapshot>? _changed;

        public LibrarySnapshot Snapshot { get; private set; } = snapshot;

        public int RefreshCalls { get; private set; }

        public int SubscriberCount { get; private set; }

        public event EventHandler<LibrarySnapshot>? Changed
        {
            add
            {
                _changed += value;
                SubscriberCount++;
            }
            remove
            {
                _changed -= value;
                SubscriberCount--;
            }
        }

        public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LibraryRefreshResult> RefreshAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(new LibraryRefreshResult(Snapshot));
        }

        public void Publish(LibrarySnapshot value)
        {
            Snapshot = value;
            _changed?.Invoke(this, value);
        }
    }
}
