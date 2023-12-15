using System;
using Crimson.Core;
using Crimson.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views;

public sealed partial class CurrentDownloadControl : UserControl
{
    private readonly LibraryManager _libraryManager;
    private readonly InstallManager _installManager;
    public CurrentDownloadControl()
    {
        InitializeComponent();
        _installManager = DependencyResolver.Resolve<InstallManager>();
        _libraryManager = DependencyResolver.Resolve<LibraryManager>();

        var gameInQueue = _installManager.CurrentInstall;
        HandleInstallationStatusChanged(gameInQueue);
        _installManager.InstallationStatusChanged += HandleInstallationStatusChanged;
        _installManager.InstallProgressUpdate += InstallationProgressUpdate;

    }

    // Handing Installtion State Change
    // This function is never run on UI Thread
    // So always make sure to use Dispatcher Queue to update UI thread
    private void HandleInstallationStatusChanged(InstallItem installItem)
    {
        try
        {
            var game = installItem;
            if (game == null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    EmptyDownloadText.Visibility = Visibility.Visible;
                    DownloadStatus.Visibility = Visibility.Collapsed;
                });
                return;
            }

            var gameInfo = _libraryManager.GetGameInfo(installItem.AppName);
            if (gameInfo == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStatus(installItem, gameInfo.AppTitle);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }

    private void UpdateStatus(InstallItem installItem, string Title)
    {
        DownloadSpeed.Text = "";
        DownloadedSize.Text = "";
        ProgressBar.IsEnabled = true;
        ProgressBar.IsIndeterminate = true;
        EmptyDownloadText.Visibility = Visibility.Collapsed;
        DownloadStatus.Visibility = Visibility.Visible;
        GameName.Text = Title;

        switch (installItem.Status)
        {
            case ActionStatus.Processing:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = installItem.ProgressPercentage;
                DownloadedSize.Text =
                    $@"{Util.ConvertMiBToGiBOrMiB(installItem.DownloadedSize)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalDownloadSizeMb)}";
                DownloadSpeed.Text = $@"{installItem.DownloadSpeedRaw} MB/s";
                break;
            case ActionStatus.Success:
                DownloadedSize.Text = "Installation Completed";
                DownloadSpeed.Text = string.Empty;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                break;
            case ActionStatus.Failed:
                DownloadedSize.Text = "Installation Failed";
                DownloadSpeed.Text = string.Empty;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                break;
            case ActionStatus.Cancelled:
                DownloadedSize.Text = "Installation Cancelled";
                DownloadSpeed.Text = string.Empty;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                break;
        }
    }

    private void InstallationProgressUpdate(InstallItem installItem)
    {
        try
        {
            if (installItem == null) return;

            if (installItem.Status != ActionStatus.Processing) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                ProgressBar.Value = installItem.ProgressPercentage;
                DownloadedSize.Text =
                    $@"{Util.ConvertMiBToGiBOrMiB(installItem.WrittenSize)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalWriteSizeMb)}";
                DownloadSpeed.Text = $@"{installItem.DownloadSpeedRaw} MB/s";
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
