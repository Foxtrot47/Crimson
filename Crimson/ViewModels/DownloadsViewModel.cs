using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Views;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace Crimson.ViewModels;

public partial class DownloadsViewModel : ObservableObject
{

    private readonly ILogger _log;
    private readonly InstallManager _installManager;
    private readonly LibraryManager _libraryManager;
    private readonly Windows.System.DispatcherQueue _dispatcherQueue;

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
    private BitmapImage? _currentInstallItemImageSource;

    [ObservableProperty]
    private string _currentInstallAction = string.Empty;

    [ObservableProperty]
    private string _currentDownloadSpeed = string.Empty;

    [ObservableProperty]
    private string _currentDownloadSize = string.Empty;

    public DownloadsViewModel()
    {
        _log = App.GetService<ILogger>();
        _dispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
        _log.Information("DownloadsPage: Loading Page");

        _installManager = App.GetService<InstallManager>();
        _libraryManager = App.GetService<LibraryManager>();

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
                    Image = Util.GetBitmapImage(gameInfo.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
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

            _log.Information("FetchHistoryItemsList: History Items: {HistoryItems}", historyItemsNames);
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
                    Image = Util.GetBitmapImage(gameInfo.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
                });
            }
            HistoryItems = itemList;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FetchHistoryItemsList: Error while fetching history items");
        }
    }


    // Handing Installation State Change
    // This function is never run on UI Thread
    // So always make sure to use Dispatcher Queue to update UI thread
    private void HandleInstallationStatusChanged(InstallItem installItem)
    {
        try
        {
            UpdateUIProperties(() =>
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

                var gameInfo = _libraryManager.GetGameInfo(installItem.AppName);
                _log.Debug("HandleInstallationStatusChanged: Game Info: {GameInfo}", gameInfo);
                CurrentInstallItem = new DownloadManagerItem
                {
                    Name = gameInfo.AppName,
                    Title = gameInfo.AppTitle,
                    InstallState = gameInfo.InstallStatus,
                    Image = Util.GetBitmapImage(gameInfo.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")
                        ?.Url)
                };
                CurrentInstallItemName = CurrentInstallItem.Title;
                CurrentInstallItemImageSource = CurrentInstallItem.Image;

                _log.Information("HandleInstallationStatusChanged: Installation Status: {Status}", installItem.Status);
                switch (installItem.Status)
                {
                    case ActionStatus.Processing:
                        DownloadProgressBarIndeterminate = false;
                        DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
                        CurrentInstallAction = $@"{installItem.Action}ing";
                        CurrentDownloadSize = $@"{Util.ConvertMiBToGiBOrMiB(installItem.WrittenSizeMiB)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalWriteSizeMb)}";
                        CurrentDownloadSpeed = $"{installItem.DownloadSpeedRawMiB} MiB /s";
                        break;
                    case ActionStatus.Paused:
                        DownloadProgressBarIndeterminate = false;
                        DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
                        CurrentInstallAction = "Paused";
                        CurrentDownloadSize = $@"{Util.ConvertMiBToGiBOrMiB(installItem.WrittenSizeMiB)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalWriteSizeMb)}";
                        CurrentDownloadSpeed = string.Empty;
                        break;
                    case ActionStatus.Cancelling:
                        DownloadProgressBarIndeterminate = true;
                        CurrentInstallAction = "Cancelling";
                        CurrentDownloadSize = string.Empty;
                        CurrentDownloadSpeed = string.Empty;
                        break;
                    case ActionStatus.Success:
                    case ActionStatus.Failed:
                    case ActionStatus.Cancelled:
                        ShowCurrentDownload = false;
                        break;
                }

                if (installItem.Status == ActionStatus.Paused)
                {
                    ShowResumeButton = true;
                    ShowPauseButton = false;
                }
                else if (installItem.Status == ActionStatus.Processing)
                {
                    ShowResumeButton = false;
                    ShowPauseButton = true;
                    EnablePauseButton = true;
                }
                else
                {
                    ShowResumeButton = false;
                    ShowPauseButton = true;
                    EnablePauseButton = false;
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception in HandleInstallationStatusChanged");
        }
    }

    private void InstallationProgressUpdate(InstallItem installItem)
    {
        // Its better not to log the progress update as it will be called very frequently
        // It can make the log file very big
        try
        {

            if (installItem == null) return;

            if (installItem.Status != ActionStatus.Processing) return;
            var result = UpdateUIProperties(() =>
            {
                DownloadProgressBarIndeterminate = false;
                DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
                CurrentDownloadSize = $@"{Util.ConvertMiBToGiBOrMiB(installItem.WrittenSizeMiB)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalWriteSizeMb)}";
                CurrentDownloadSpeed = $@"{installItem.DownloadSpeedRawMiB} MiB/s";
            });
            _log.Debug("InstallationProgressUpdate: Progress Updated: {Result}", installItem.WrittenSizeMiB);
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
        _installManager.CancelInstall(_currentInstallItem.Name);
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
        return _dispatcherQueue.TryEnqueue(() => updateAction());

    }
}


