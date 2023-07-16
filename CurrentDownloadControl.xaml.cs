using System;
using Epsilon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Epsilon;

public sealed partial class CurrentDownloadControl : UserControl
{
    public CurrentDownloadControl()
    {
        InitializeComponent();
        var gameInQueue = InstallManager.CurrentInstall;
        HandleInstallationStatusChanged(gameInQueue);
        InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;
        InstallManager.InstallProgressUpdate += InstallationProgressUpdate;
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

            var gameInfo = StateManager.GetGameInfo(installItem.AppName);
            if (gameInfo == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStatus(installItem, game, gameInfo);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }

    private void UpdateStatus(InstallItem installItem, InstallItem game, Game gameInfo)
    {
        DownloadSpeed.Text = "";
        DownloadedSize.Text = "";
        ProgressBar.IsEnabled = true;
        ProgressBar.IsIndeterminate = true;
        EmptyDownloadText.Visibility = Visibility.Collapsed;
        DownloadStatus.Visibility = Visibility.Visible;
        GameName.Text = gameInfo.Title;

        switch (game.Status)
        {
            case ActionStatus.Processing:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = game.ProgressPercentage;
                DownloadedSize.Text =
                    $@"{Util.ConvertMiBToGiBOrMiB(installItem.DownloadedSize)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalDownloadSizeMb)}";
                DownloadSpeed.Text = $@"{game.DownloadSpeedRaw} MB/s";
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
                    $@"{Util.ConvertMiBToGiBOrMiB(installItem.DownloadedSize)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalDownloadSizeMb)}";
                DownloadSpeed.Text = $@"{installItem.DownloadSpeedRaw} MB/s";
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
