using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUiApp.Core;

namespace WinUiApp
{
    /// <summary>
    /// Page where we list current and past downloads
    /// </summary>
    public sealed partial class DownloadsPage : Page
    {
        private DownloadManagerItem _currentInstallItem = new DownloadManagerItem();
        private ObservableCollection<DownloadManagerItem> queueItems = new();
        public DownloadsPage()
        {
            this.InitializeComponent();
            DataContext = _currentInstallItem;
            if (InstallManager.CurrentInstall?.AppName == null)
                ActiveDownloadSection.Visibility = Visibility.Collapsed;

            var gameInQueue = InstallManager.CurrentInstall;
            HandleInstallationStatusChanged(gameInQueue);
            FetchQueueItemsList();
            InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;
        }

        // Handing Installtion State Change
        // This function is never run on UI Thread
        // So always make sure to use Dispatcher Queue to update UI thread
        private void HandleInstallationStatusChanged(InstallItem installItem)
        {
            try
            {
                if (installItem == null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ActiveDownloadSection.Visibility = Visibility.Collapsed;
                    });
                    FetchQueueItemsList();
                    return;
                }
                DispatcherQueue.TryEnqueue(() =>
                {
                    ActiveDownloadSection.Visibility = Visibility.Visible;
                    DownloadProgressBar.IsIndeterminate = true;

                    var gameInfo = StateManager.GetGameInfo(installItem.AppName);
                    _currentInstallItem = new DownloadManagerItem
                    {
                        Name = gameInfo.Name,
                        Title = gameInfo.Title,
                        InstallState = gameInfo.State,
                        Image = Util.GetBitmapImage(gameInfo.Images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")
                            ?.Url)
                    };
                    CurrentDownloadTitle.Text = _currentInstallItem.Title;
                    CurrentDownloadImage.Source = _currentInstallItem.Image;
                });

                switch (installItem.Status)
                {
                    case ActionStatus.Processing:
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            DownloadProgressBar.IsIndeterminate = false;
                            DownloadProgressBar.Value = Convert.ToDouble(installItem.ProgressPercentage);
                            CurrentDownloadAction.Text = $@"{installItem.Action}ing";
                            CurrentDownloadedSize.Text = $@"{Util.ConvertMiBToGiBOrMiB(installItem.DownloadedSize)} of {Util.ConvertMiBToGiBOrMiB(installItem.TotalDownloadSizeMb)}";
                            CurrentDownloadSpeed.Text = $@"{installItem.DownloadSpeedRaw} MiB/s";
                        });
                        break;
                    case ActionStatus.Success:
                    case ActionStatus.Failed:
                    case ActionStatus.Cancelled:
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            CurrentDownloadAction.Text = $@"{installItem.Action} {installItem.Status}";
                            CurrentDownloadedSize.Text = "";
                            DownloadProgressBar.IsIndeterminate = false;
                            DownloadProgressBar.Value = 100;
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void CancelInstallButton_OnClick(object sender, RoutedEventArgs e)
        {
            InstallManager.CancelInstall(_currentInstallItem.Name);
        }

        private void FetchQueueItemsList()
        {
            var queueItemNames = InstallManager.GetQueueItemNames();
            if (queueItemNames == null || queueItemNames.Count < 1) return;

            DispatcherQueue.TryEnqueue(() => queueItems.Clear());

            ObservableCollection<DownloadManagerItem> itemList = new();
            foreach (var queueItemName in queueItemNames)
            {

                var gameInfo = StateManager.GetGameInfo(queueItemName);
                if (gameInfo is null) continue;
                itemList.Add(new DownloadManagerItem()
                {
                    Name = queueItemName,
                    Title = gameInfo.Title,
                    Image = Util.GetBitmapImage(gameInfo.Images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
                });
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                queueItems = itemList;
                InstallQueueListView.ItemsSource = queueItems;
            });
        }
    }
    public class DownloadManagerItem
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public BitmapImage Image { get; set; }
        public Game.InstallState InstallState { get; set; }
    }
}
