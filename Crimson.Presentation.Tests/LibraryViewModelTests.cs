using Xunit;
using Crimson.Core;
using Crimson.Presentation;

namespace Crimson.Presentation.Tests;

public sealed class LibraryViewModelTests
{
    [Fact]
    public async Task ActivationLoadsSnapshotAndOwnsSubscription()
    {
        var service = new StubLibraryService(new LibrarySnapshot(1,
        [
            new GameSummary("game", "Game", new Uri("https://example.test/game.png"), "1.0", true)
        ]));
        var navigation = new NavigationService();
        var viewModel = new LibraryViewModel(service, new ImmediateUiDispatcher(), navigation);

        await viewModel.ActivateAsync();
        await viewModel.ActivateAsync();

        Assert.Equal(1, service.GetCalls);
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
        var service = new StubLibraryService(new LibrarySnapshot(1, []));
        var viewModel = new LibraryViewModel(
            service,
            new ImmediateUiDispatcher(),
            new NavigationService());
        await viewModel.ActivateAsync();

        service.Publish(new LibrarySnapshot(2,
        [
            new GameSummary("new", "New Game", null, "2.0", false)
        ]));
        Assert.Equal("New Game", Assert.Single(viewModel.Games).Title);

        viewModel.Deactivate();
        service.Publish(new LibrarySnapshot(3, []));
        Assert.Single(viewModel.Games);
    }

    private sealed class StubLibraryService(LibrarySnapshot snapshot) : ILibraryService
    {
        private EventHandler<LibrarySnapshot>? _changed;

        public int GetCalls { get; private set; }

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

        public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(snapshot);
        }

        public void Publish(LibrarySnapshot value) => _changed?.Invoke(this, value);
    }
}
