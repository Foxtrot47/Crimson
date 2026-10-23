using Crimson.Resources;
using System;
using Crimson.Core;
using Crimson.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Crimson.Views;

public sealed class DownloadSummary
{
    public bool HasActiveDownload { get; init; }
    public bool IsIndeterminate { get; init; }
    public int ProgressPercentage { get; init; }
    public ActionType Action { get; init; }
    public ActionStatus Status { get; init; }
    public string ToolTip { get; init; } = UiStrings.NoDownloadsInQueue;
}

public sealed partial class CurrentDownloadControl : UserControl, IDisposable
{
    public event Action<DownloadSummary>? SummaryChanged;

    private readonly ILogger _log;
    private readonly LibraryManager _libraryManager;
    private readonly InstallManager _installManager;

    public CurrentDownloadControl()
    {
        InitializeComponent();
        _log = App.GetService<ILogger>();
        _installManager = App.GetService<InstallManager>();
        _libraryManager = App.GetService<LibraryManager>();

        HandleOperationUpdated(_installManager.CurrentOperation);
        _installManager.OperationUpdated += HandleOperationUpdated;
        _installManager.TransferRatesUpdated += HandleTransferRatesUpdated;
    }

    public void Dispose()
    {
        _installManager.OperationUpdated -= HandleOperationUpdated;
        _installManager.TransferRatesUpdated -= HandleTransferRatesUpdated;
    }

    private void HandleTransferRatesUpdated(OperationSnapshot operation) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            var current = _installManager.CurrentOperation;
            if (current?.OperationId == operation.OperationId) ApplyDownloadSpeed(current);
        });

    public void PublishCurrentSummary() => HandleOperationUpdated(_installManager.CurrentOperation);

    private void HandleOperationUpdated(OperationSnapshot? changedOperation)
    {
        try
        {
            var operation = _installManager.CurrentOperation ?? changedOperation;
            if (operation is null || !operation.IsActive)
            {
                DispatcherQueue.TryEnqueue(ShowEmptyState);
                return;
            }

            var gameInfo = _libraryManager.GetGameInfo(operation.AppName);
            if (gameInfo is null) return;

            DispatcherQueue.TryEnqueue(() => ApplyOperation(operation, gameInfo.AppTitle));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to update operation status");
        }
    }

    private void ShowEmptyState()
    {
        EmptyDownloadText.Visibility = Visibility.Visible;
        DownloadStatus.Visibility = Visibility.Collapsed;
        SummaryChanged?.Invoke(new DownloadSummary());
    }

    private void ApplyOperation(OperationSnapshot operation, string title)
    {
        var presentation = OperationPresenter.Present(operation);
        EmptyDownloadText.Visibility = Visibility.Collapsed;
        DownloadStatus.Visibility = Visibility.Visible;
        GameName.Text = title;
        OperationStatus.Text = presentation.Headline;
        ProgressBar.IsEnabled = true;
        ProgressBar.IsIndeterminate = presentation.IsIndeterminate;
        ProgressBar.Value = presentation.ProgressPercentage;
        DownloadedSize.Text = presentation.Detail;
        ApplyDownloadSpeed(operation);

        SummaryChanged?.Invoke(new DownloadSummary
        {
            HasActiveDownload = presentation.IsActive,
            IsIndeterminate = presentation.IsIndeterminate,
            ProgressPercentage = presentation.ProgressPercentage,
            Action = operation.Action,
            Status = operation.Status,
            ToolTip = UiStrings.Format(UiStrings.GameOperationFormat, title, presentation.Headline.ToLowerInvariant())
        });
    }

    private void ApplyDownloadSpeed(OperationSnapshot operation)
    {
        var transferring = operation.Phase == OperationPhase.Transferring &&
            operation.Status is ActionStatus.OnGoing or ActionStatus.Processing;
        DownloadSpeed.Text = transferring && !OperationPresenter.IsRetryingTransfer(operation) && operation.DownloadSpeedMiB > 0
            ? Crimson.ViewModels.DownloadsViewModel.FormatTransferRate(operation.DownloadSpeedMiB)
            : string.Empty;
    }
}
