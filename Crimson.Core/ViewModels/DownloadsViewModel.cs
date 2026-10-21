using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class DownloadsViewModel : ObservableObject, IDisposable
{

    private readonly ILogger _log;
    private readonly LauncherInstaller _installManager;
    private readonly LauncherLibrary _libraryManager;
    private readonly IUiDispatcher _uiDispatcher;

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
        LauncherInstaller installManager,
        LauncherLibrary libraryManager,
        IUiDispatcher uiDispatcher)
    {
        _log = logger;
        _uiDispatcher = uiDispatcher;
        _log.Information("DownloadsPage: Loading Page");

        _installManager = installManager;
        _libraryManager = libraryManager;

        var gameInQueue = _installManager.CurrentInstall;
        HandleInstallationStatusChanged(gameInQueue);
        FetchQueueItemsList();
        FetchHistoryItemsList();
        _installManager.InstallationStatusChanged += HandleInstallationStatusChanged;
        _installManager.InstallProgressUpdate += InstallationProgressUpdate;
    }

    private void FetchQueueItemsList()
    {
        try
        {
            QueueItems.Clear();
            var queueItemNames = _installManager.GetQueueItemNames();
            if (queueItemNames == null || queueItemNames.Count < 1) return;

            ObservableCollection<DownloadManagerItem> itemList = new();
            foreach (var queueItemName in queueItemNames)
            {

                var gameInfo = _libraryManager.GetGameInfo(queueItemName);
                if (gameInfo is null) continue;
                itemList.Add(new DownloadManagerItem()
                {
                    Name = queueItemName,
                    Title = gameInfo.AppTitle,
                    ImageUrl = gameInfo.CoverImageUrl
                });

            }
            QueueItems = itemList;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FetchQueueItemsList: Exception occurred");
        }
    }
    private void FetchHistoryItemsList()
    {
        try
        {
            var historyItemsNames = _installManager.GetHistoryItemsNames();
            if (historyItemsNames == null || historyItemsNames.Count < 1) return;

            _log.Debug("FetchHistoryItemsList: Loaded {Count} history items", historyItemsNames.Count);
            HistoryItems.Clear();

            ObservableCollection<DownloadManagerItem> itemList = new();

            foreach (var historyItemName in historyItemsNames)
            {

                var gameInfo = _libraryManager.GetGameInfo(historyItemName);
                if (gameInfo is null) continue;
                itemList.Add(new DownloadManagerItem()
                {
                    Name = historyItemName,
                    Title = gameInfo.AppTitle,
                    ImageUrl = gameInfo.CoverImageUrl
                });
            }
            HistoryItems = itemList;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FetchHistoryItemsList: Error while fetching history items");
        }
    }


    private void HandleInstallationStatusChanged(InstallItem? installItem)
    {
        try
        {
            UpdateUIProperties(() => RefreshAndApplyStatus(_installManager.CurrentInstall ?? installItem));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception in HandleInstallationStatusChanged");
        }
    }

    private void RefreshAndApplyStatus(InstallItem? installItem)
    {
        _log.Information("HandleInstallationStatusChanged: Handling Installation Status Change");
        FetchQueueItemsList();
        FetchHistoryItemsList();
        if (installItem == null)
        {
            _log.Information("HandleInstallationStatusChanged: No installation in progress");
            ShowCurrentDownload = false;
            return;
        }

        ShowCurrentDownload = true;
        DownloadProgressBarIndeterminate = true;
        SetCurrentInstallItem(installItem);
        ApplyInstallationStatus(installItem);
        UpdatePauseResumeControls(installItem.Status);
    }

    private void SetCurrentInstallItem(InstallItem installItem)
    {
        var gameInfo = _libraryManager.GetGameInfo(installItem.AppName);
        _log.Debug("HandleInstallationStatusChanged: Loaded {AppName}", installItem.AppName);
        CurrentInstallItem = new DownloadManagerItem
        {
            Name = installItem.AppName,
            Title = gameInfo?.AppTitle ?? GameId.Parse(installItem.AppName).LocalId,
            InstallState = gameInfo?.LocalAppState?.InstallStatus ?? InstallState.NotInstalled,
            ImageUrl = gameInfo?.CoverImageUrl
        };
        CurrentInstallItemName = CurrentInstallItem.Title;
        CurrentInstallItemImageUrl = CurrentInstallItem.ImageUrl;
    }

    private void ApplyInstallationStatus(InstallItem installItem)
    {
        _log.Information("HandleInstallationStatusChanged: Installation Status: {Status}", installItem.Status);
        switch (installItem.Status)
        {
            case ActionStatus.Processing:
                ApplyProcessingState(installItem);
                break;
            case ActionStatus.Paused:
                ApplyPausedState(installItem);
                break;
            case ActionStatus.Cancelling:
                ApplyCancellingState();
                break;
            case ActionStatus.Success:
            case ActionStatus.Failed:
            case ActionStatus.Cancelled:
                ShowCurrentDownload = false;
                break;
        }
    }

    private void ApplyProcessingState(InstallItem installItem)
    {
        DownloadProgressBarIndeterminate = false;
        DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
        CurrentInstallAction = $@"{installItem.Action}ing";
        CurrentDownloadSize = FormatProgressSize(installItem);
        CurrentDownloadSpeed = $"{installItem.DownloadSpeedRawMiB} MiB /s";
    }

    private void ApplyPausedState(InstallItem installItem)
    {
        DownloadProgressBarIndeterminate = false;
        DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
        CurrentInstallAction = "Paused";
        CurrentDownloadSize = FormatProgressSize(installItem);
        CurrentDownloadSpeed = string.Empty;
    }

    private void ApplyCancellingState()
    {
        DownloadProgressBarIndeterminate = true;
        CurrentInstallAction = "Cancelling";
        CurrentDownloadSize = string.Empty;
        CurrentDownloadSpeed = string.Empty;
    }

    private static string FormatProgressSize(InstallItem installItem) =>
        $@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";

    private void UpdatePauseResumeControls(ActionStatus status)
    {
        if (!_installManager.SupportsPause)
        {
            ShowResumeButton = false;
            ShowPauseButton = false;
            return;
        }
        if (status == ActionStatus.Paused)
        {
            ShowResumeButton = true;
            ShowPauseButton = false;
            return;
        }

        ShowResumeButton = false;
        ShowPauseButton = true;
        EnablePauseButton = status == ActionStatus.Processing;
    }

    private void InstallationProgressUpdate(InstallItem installItem)
    {
        // Its better not to log the progress update as it will be called very frequently
        // It can make the log file very big
        try
        {

            if (installItem == null || _installManager.CurrentInstall?.AppName != installItem.AppName) return;

            if (installItem.Status != ActionStatus.Processing) return;
            _ = UpdateUIProperties(() =>
            {
                DownloadProgressBarIndeterminate = false;
                DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
                CurrentDownloadSize = $@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";
                CurrentDownloadSpeed = $@"{installItem.DownloadSpeedRawMiB} MiB/s";
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "InstallationProgressUpdate: Error while updating progress");
        }
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
        var appName = CurrentInstallItem.Name;
        Task.Run(() => _installManager.PauseInstall(appName));
    }

    [RelayCommand]
    private void ResumeInstall()
    {
        var appName = CurrentInstallItem.Name;
        Task.Run(() => _installManager.ResumeInstall(appName));
    }

    private bool UpdateUIProperties(Action updateAction)
    {
        return _uiDispatcher.TryEnqueue(updateAction);

    }
    public void Dispose()
    {
        _installManager.InstallationStatusChanged -= HandleInstallationStatusChanged;
        _installManager.InstallProgressUpdate -= InstallationProgressUpdate;
    }

}
