using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUiApp;

public sealed partial class CurrentDownloadControl : UserControl
{
    public CurrentDownloadControl()
    {
        InitializeComponent();
        InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;
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

            var gameInfo = StateManager.StateManager.GetGameInfo(installItem.AppName);
            if (gameInfo == null) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                DownloadSpeed.Text = "";
                DownloadedSize.Text = "";
                ProgressBar.IsEnabled = true;
                ProgressBar.IsIndeterminate = true;
                EmptyDownloadText.Visibility = Visibility.Collapsed;
                DownloadStatus.Visibility = Visibility.Visible;
                GameName.Text = gameInfo.Title;
            });

            if (game.Status == ActionStatus.Processing)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = game.ProgressPercentage;
                    DownloadedSize.Text =
                        $@"{installItem.DownloadedSize} MiB of  {installItem.TotalDownloadSizeMb} MiB";
                    DownloadSpeed.Text = $@"{game.DownloadSpeedRaw} MB/s";
                });
                return;
            }

            if (game.Status != ActionStatus.Success ||
                game.Action is not (ActionType.Install or ActionType.Update or ActionType.Repair)) return;
            DispatcherQueue.TryEnqueue(() => { DownloadStatus.Visibility = Visibility.Collapsed; });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
        }
    }
}
