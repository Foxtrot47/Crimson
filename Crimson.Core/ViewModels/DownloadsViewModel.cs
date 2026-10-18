using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.ViewModels;

public partial class DownloadsViewModel : ObservableObject, IDisposable
{

    private readonly ILogger _log;
    private readonly InstallManager _installManager;
    private readonly LibraryManager _libraryManager;
    private readonly IUiDispatcher _uiDispatcher;
    private IReadOnlyList<OperationSnapshot> _queueSnapshot = [];
    private IReadOnlyList<OperationSnapshot> _historySnapshot = [];

    [ObservableProperty]
    private DownloadManagerItem _currentInstallItem = new DownloadManagerItem();

    [ObservableProperty]
    private ObservableCollection<DownloadManagerItem> _queueItems = new();

    [ObservableProperty]
    private ObservableCollection<DownloadManagerItem> _historyItems = new();

    [ObservableProperty]
    private bool _showPauseButton = false;

    [ObservableProperty]
    private bool _showResumeButton = false;

    [ObservableProperty]
    private bool _enablePauseButton = false;

    [ObservableProperty]
    private bool _enableCancelButton = false;

    [ObservableProperty]
    private bool _showCurrentDownload = false;

    [ObservableProperty]
    private bool _downloadProgressBarIndeterminate = false;

    [ObservableProperty]
    private double _downloadProgressBarValue;

    [ObservableProperty]
    private string _currentInstallItemName = string.Empty;

    [ObservableProperty]
    private string? _currentInstallItemImageUrl;

    [ObservableProperty]
    private string _currentInstallAction = string.Empty;

    [ObservableProperty]
    private string _currentDownloadSpeed = string.Empty;

    [ObservableProperty]
    private string _currentDownloadSize = string.Empty;

    public DownloadsViewModel(
        ILogger logger,
        InstallManager installManager,
        LibraryManager libraryManager,
        IUiDispatcher uiDispatcher)
    {
        _log = logger;
        _uiDispatcher = uiDispatcher;
        _log.Information("DownloadsPage: Loading Page");

        _installManager = installManager;
        _libraryManager = libraryManager;

        HandleOperationUpdated(_installManager.CurrentOperation);
        _installManager.OperationUpdated += HandleOperationUpdated;
    }

    private void FetchQueueItemsList(IReadOnlyList<OperationSnapshot> operations)
    {
        try
        {
            ObservableCollection<DownloadManagerItem> itemList = new();
            for (var index = 0; index < operations.Count; index++)
            {
                var operation = operations[index];
                var gameInfo = _libraryManager.GetGameInfo(operation.AppName);
                if (gameInfo is null) continue;
                itemList.Add(new DownloadManagerItem
                {
                    Name = operation.AppName,
                    Title = gameInfo.AppTitle,
                    ImageUrl = gameInfo.Metadata.KeyImages
                        .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url,
                    StatusText = $"{OperationPresenter.Present(operation).Headline} · Position {index + 1}"
                });
            }
            QueueItems = itemList;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FetchQueueItemsList: Exception occurred");
        }
    }
    private void FetchHistoryItemsList(IReadOnlyList<OperationSnapshot> operations)
    {
        try
        {
            _log.Debug("FetchHistoryItemsList: Loaded {Count} history items", operations.Count);
            ObservableCollection<DownloadManagerItem> itemList = new();
            foreach (var operation in operations)
            {
                var gameInfo = _libraryManager.GetGameInfo(operation.AppName);
                if (gameInfo is null) continue;
                itemList.Add(new DownloadManagerItem
                {
                    Name = operation.AppName,
                    Title = gameInfo.AppTitle,
                    ImageUrl = gameInfo.Metadata.KeyImages
                        .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url,
                    StatusText = OperationPresenter.Present(operation).Headline,
                    FailureText = operation.Failure is null
                        ? string.Empty
                        : $"{operation.Failure.Message} {operation.Failure.SuggestedAction}",
                    HasFailure = operation.Failure is not null,
                    CanRetry = operation.Failure?.CanRetry == true,
                    Operation = operation
                });
            }
            HistoryItems = itemList;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FetchHistoryItemsList: Error while fetching history items");
        }
    }


    private void HandleOperationUpdated(OperationSnapshot? _)
    {
        try
        {
            UpdateUIProperties(() => RefreshAndApplyStatus(_installManager.CurrentOperation));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception in HandleOperationUpdated");
        }
    }

    private void RefreshAndApplyStatus(OperationSnapshot? operation)
    {
        RefreshOperationLists();
        if (operation is null || !operation.IsActive)
        {
            ShowCurrentDownload = false;
            return;
        }

        ShowCurrentDownload = true;
        SetCurrentInstallItem(operation);
        ApplyOperationPresentation(operation);
    }

    private void RefreshOperationLists()
    {
        var queue = _installManager.GetQueueOperations();
        if (HasOperationListChanged(_queueSnapshot, queue))
        {
            FetchQueueItemsList(queue);
            _queueSnapshot = queue;
        }

        var history = _installManager.GetHistoryOperations();
        if (HasOperationListChanged(_historySnapshot, history))
        {
            FetchHistoryItemsList(history);
            _historySnapshot = history;
        }
    }

    internal static bool HasOperationListChanged(
        IReadOnlyList<OperationSnapshot> previous,
        IReadOnlyList<OperationSnapshot> current)
    {
        if (previous.Count != current.Count) return true;

        for (var index = 0; index < previous.Count; index++)
        {
            if (!HasSameListPresentation(previous[index], current[index]))
                return true;
        }
        return false;
    }

    private static bool HasSameListPresentation(
        OperationSnapshot left,
        OperationSnapshot right) =>
        left.AppName == right.AppName &&
        left.Action == right.Action &&
        left.Status == right.Status &&
        left.Phase == right.Phase &&
        left.IsRetrying == right.IsRetrying &&
        left.StatusMessage == right.StatusMessage &&
        left.Failure == right.Failure;

    private void SetCurrentInstallItem(OperationSnapshot operation)
    {
        var gameInfo = _libraryManager.GetGameInfo(operation.AppName);
        if (gameInfo is null)
        {
            ShowCurrentDownload = false;
            return;
        }
        _log.Debug("HandleOperationUpdated: Loaded {AppName}", gameInfo.AppName);
        CurrentInstallItem = new DownloadManagerItem
        {
            Name = gameInfo.AppName,
            Title = gameInfo.AppTitle,
            InstallState = gameInfo.LocalAppState?.InstallStatus ?? InstallState.NotInstalled,
            ImageUrl = gameInfo.Metadata.KeyImages
                .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
        };
        CurrentInstallItemName = CurrentInstallItem.Title;
        CurrentInstallItemImageUrl = CurrentInstallItem.ImageUrl;
    }

    private void ApplyOperationPresentation(OperationSnapshot operation)
    {
        var presentation = OperationPresenter.Present(operation);
        DownloadProgressBarIndeterminate = presentation.IsIndeterminate;
        DownloadProgressBarValue = presentation.ProgressPercentage;
        CurrentInstallAction = presentation.Headline;
        CurrentDownloadSize = presentation.Detail;
        CurrentDownloadSpeed = operation.Phase == OperationPhase.Transferring && operation.DownloadSpeedMiB > 0
            ? $"{operation.DownloadSpeedMiB:0.##} MiB/s"
            : string.Empty;
        ShowResumeButton = presentation.CanResume;
        ShowPauseButton = !presentation.CanResume;
        EnablePauseButton = presentation.CanPause;
        EnableCancelButton = presentation.CanCancel;
    }

    public bool RetryFailedOperation(DownloadManagerItem? item)
    {
        if (item?.Operation is not { Status: ActionStatus.Failed } operation)
            return false;
        return _installManager.RetryFailedOperation(operation.AppName);
    }

    [RelayCommand]
    private void CancelInstall()
    {
        _log.Information("CancelInstallButton_OnClick: Cancelling Installation");
        _installManager.CancelInstall(CurrentInstallItem.Name);
    }

    [RelayCommand]
    private void PauseInstall()
    {
        Task.Run(() => _installManager.PauseInstall());
    }

    [RelayCommand]
    private void ResumeInstall()
    {
        Task.Run(() => _installManager.ResumeInstall());
    }

    private bool UpdateUIProperties(Action updateAction)
    {
        return _uiDispatcher.TryEnqueue(updateAction);

    }
    public void Dispose()
    {
        _installManager.OperationUpdated -= HandleOperationUpdated;
    }

}


