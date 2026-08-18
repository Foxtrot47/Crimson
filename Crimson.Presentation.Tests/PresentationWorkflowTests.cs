using Crimson.Core;
using Crimson.Models;
using Crimson.Presentation;
using Xunit;

namespace Crimson.Presentation.Tests;

public sealed class PresentationWorkflowTests
{
    [Fact]
    public async Task GameWorkflowMapsActionsAndReleasesSubscriptions()
    {
        var games = new StubGameWorkflowService(
            Game("game", InstallState.NeedUpdate, "C:/Games/Game"));
        var viewModel = new GameInfoViewModel(
            games,
            new StubInstallDialog(),
            new StubFolderPicker(null),
            new ImmediateUiDispatcher());

        await viewModel.LoadAsync("game");

        Assert.Equal("Update", viewModel.PrimaryActionButtonText);
        Assert.Equal(GameActionIcon.Update, viewModel.PrimaryActionIcon);
        Assert.Equal(1, games.GameSubscriberCount);
        Assert.Equal(1, games.OperationSubscriberCount);
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);
        Assert.Equal(ActionType.Update, Assert.Single(games.Enqueued).Action);

        games.PublishOperation(new InstallOperationSnapshot(
            "game", "Game", null, ActionType.Update, ActionStatus.Processing,
            42, 4, 10, 3));
        Assert.Equal("42%", viewModel.PrimaryActionButtonText);
        Assert.Equal(42, viewModel.ProgressValue);

        viewModel.Deactivate();
        Assert.Equal(0, games.GameSubscriberCount);
        Assert.Equal(0, games.OperationSubscriberCount);
    }

    [Fact]
    public async Task DownloadsWorkflowOwnsCurrentQueueAndHistorySubscriptions()
    {
        var games = new StubGameWorkflowService(Game("game", InstallState.Installed, "games"));
        games.Queued = [games.GetGame("game")!];
        games.History = [games.GetGame("game")!];
        games.Current = new InstallOperationSnapshot(
            "game", "Game", null, ActionType.Install, ActionStatus.Processing,
            25, 2, 8, 4);
        var current = new CurrentOperationViewModel(games, new ImmediateUiDispatcher());
        using var viewModel = new DownloadsViewModel(games, new ImmediateUiDispatcher(), current);

        await viewModel.ActivateAsync();

        Assert.Single(viewModel.QueueItems);
        Assert.Single(viewModel.HistoryItems);
        Assert.True(current.IsVisible);
        Assert.Equal(25, current.ProgressPercentage);
        Assert.Equal(2, games.OperationSubscriberCount);

        viewModel.Deactivate();
        Assert.Equal(0, games.OperationSubscriberCount);
    }

    [Fact]
    public async Task InstallDialogLoadsContentSelectsFolderAndQueuesSelectedItems()
    {
        var games = new StubGameWorkflowService(Game("game", InstallState.NotInstalled, null));
        games.Dlcs = [Game("dlc", InstallState.NotInstalled, null, isDlc: true)];
        games.Sizes["game"] = new InstallContentSize(100, 200);
        games.Sizes["dlc"] = new InstallContentSize(50, 75);
        games.Drive = new DriveSpaceSnapshot(10_000, 20_000);
        var dialog = new StubInstallDialog();
        var viewModel = new AppInstallDialogViewModel(
            games,
            new StubSettingsService(),
            new StubFolderPicker("D:/Games"),
            dialog);

        await viewModel.LoadAsync("game");
        await viewModel.SelectLocationCommand.ExecuteAsync(null);
        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine("D:/Games", "Game"), viewModel.InstallLocation);
        Assert.Equal(2, games.Enqueued.Count);
        Assert.Equal(["game", "dlc"], games.Enqueued.Select(item => item.AppName));
        Assert.True(dialog.Closed);
    }

    private static GamePresentationData Game(
        string appName,
        InstallState state,
        string? installPath,
        bool isDlc = false) => new(
        appName,
        appName == "game" ? "Game" : "DLC",
        new Uri("https://example.test/game.png"),
        "1.0",
        state,
        installPath,
        isDlc);

    private sealed class StubGameWorkflowService(params GamePresentationData[] games)
        : IGameWorkflowService
    {
        private EventHandler<GamePresentationData>? _gameChanged;
        private EventHandler<InstallOperationSnapshot?>? _operationChanged;
        private readonly Dictionary<string, GamePresentationData> _games =
            games.ToDictionary(game => game.AppName, StringComparer.Ordinal);

        public int GameSubscriberCount { get; private set; }
        public int OperationSubscriberCount { get; private set; }
        public List<InstallOperationRequest> Enqueued { get; } = [];
        public Dictionary<string, InstallContentSize> Sizes { get; } = [];
        public IReadOnlyList<GamePresentationData> Dlcs { get; set; } = [];
        public IReadOnlyList<GamePresentationData> Queued { get; set; } = [];
        public IReadOnlyList<GamePresentationData> History { get; set; } = [];
        public DriveSpaceSnapshot? Drive { get; set; }
        public InstallOperationSnapshot? Current { get; set; }

        public event EventHandler<GamePresentationData>? GameChanged
        {
            add { _gameChanged += value; GameSubscriberCount++; }
            remove { _gameChanged -= value; GameSubscriberCount--; }
        }

        public event EventHandler<InstallOperationSnapshot?>? OperationChanged
        {
            add { _operationChanged += value; OperationSubscriberCount++; }
            remove { _operationChanged -= value; OperationSubscriberCount--; }
        }

        public InstallOperationSnapshot? CurrentOperation => Current;
        public GamePresentationData? GetGame(string appName) =>
            _games.GetValueOrDefault(appName);
        public IReadOnlyList<GamePresentationData> GetDlcs(string appName) => Dlcs;
        public IReadOnlyList<GamePresentationData> GetQueuedGames() => Queued;
        public IReadOnlyList<GamePresentationData> GetHistoryGames() => History;
        public Task LaunchAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<InstallCommandResult> EnqueueAsync(
            InstallOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Enqueued.Add(request);
            return Accepted();
        }
        public Task<InstallCommandResult> CancelAsync(
            string appName,
            CancellationToken cancellationToken = default) => Accepted();
        public Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default) => Accepted();
        public Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default) => Accepted();
        public Task<InstallContentSize> GetContentSizeAsync(
            string appName,
            CancellationToken cancellationToken = default) => Task.FromResult(Sizes[appName]);
        public Task<DriveSpaceSnapshot?> GetDriveSpaceAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(Drive);

        public void PublishOperation(InstallOperationSnapshot? operation)
        {
            Current = operation;
            _operationChanged?.Invoke(this, operation);
        }

        private static Task<InstallCommandResult> Accepted() => Task.FromResult(
            new InstallCommandResult(InstallCommandOutcome.Accepted));
    }

    private sealed class StubInstallDialog : IInstallDialogService
    {
        public bool Closed { get; private set; }
        public Task ShowAsync(string appName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public void Close() => Closed = true;
    }

    private sealed class StubFolderPicker(string? selected) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(
            string? suggestedPath,
            CancellationToken cancellationToken = default) => Task.FromResult(selected);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings("C:/Games"));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
