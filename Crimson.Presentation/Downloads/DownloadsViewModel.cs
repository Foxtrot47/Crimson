using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.Presentation;

public partial class DownloadsViewModel : ObservableObject, IActivatable, IDisposable
{
    private readonly IGameWorkflowService _games;
    private readonly IUiDispatcher _dispatcher;
    private bool _active;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<DownloadQueueItemViewModel> _queueItems = [];

    [ObservableProperty]
    private ObservableCollection<DownloadQueueItemViewModel> _historyItems = [];

    public DownloadsViewModel(
        IGameWorkflowService games,
        IUiDispatcher dispatcher,
        CurrentOperationViewModel currentOperation)
    {
        _games = games;
        _dispatcher = dispatcher;
        CurrentOperation = currentOperation;
    }

    public CurrentOperationViewModel CurrentOperation { get; }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
            return;
        _active = true;
        _games.OperationChanged += OnOperationChanged;
        await CurrentOperation.ActivateAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _games.OperationChanged -= OnOperationChanged;
        CurrentOperation.Deactivate();
    }

    private void OnOperationChanged(object? sender, InstallOperationSnapshot? operation) =>
        _ = RefreshAsync(CancellationToken.None);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var queue = _games.GetQueuedGames().Select(ToItem).ToArray();
            var history = _games.GetHistoryGames().Select(ToItem).ToArray();
            await _dispatcher.InvokeAsync(() =>
            {
                QueueItems = new ObservableCollection<DownloadQueueItemViewModel>(queue);
                HistoryItems = new ObservableCollection<DownloadQueueItemViewModel>(history);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static DownloadQueueItemViewModel ToItem(GamePresentationData game) => new(
        game.AppName,
        game.Title,
        game.ImageUri,
        game.InstallState);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Deactivate();
    }
}
