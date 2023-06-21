using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using WinUiApp.StateManager;

namespace WinUiApp
{
    /// <summary>
    /// Page for Showing Details of individual game and allowing play
    /// download and other options
    /// </summary>
    public sealed partial class GameInfoPage : Page
    {
        public Game Game { get; set; }
        private bool HasDownloadStarted { get; set; }

        public GameInfoPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Game = StateManager.StateManager.GetGameInfo((string)e.Parameter);
            var gameImage = Game.Images.FirstOrDefault(i => i.Type == "DieselGameBox");
            TitleImage.SetValue(Image.SourceProperty, gameImage != null ? new BitmapImage(new Uri(gameImage.Url)) : null);
            InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;

        }

        private void DownloadButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                DownloadButton.IsEnabled = false;
                DownloadButtonText.Text = "Pending...";
                DownloadProgressRing.Visibility = Visibility.Visible;
                DownloadProgressRing.IsIndeterminate = true;
                DownloadButtonIcon.Visibility = Visibility.Collapsed;
                var installItem = new InstallItem(Game.Name,
                    ActionType.Install,
                    $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\games");

                InstallManager.AddToQueue(installItem);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                DownloadProgressRing.Visibility = Visibility.Collapsed;
                DownloadButton.IsEnabled = true;
            }
        }

        // Handing Installtion State Change
        // This function is never run on UI Thread
        // So always make sure to use Dispatcher Queue to update UI thread
        private void HandleInstallationStatusChanged(InstallItem installItem)
        {
            try
            {
                var game = installItem;
                if (game.AppName != Game.Name)
                    game = InstallManager.GameGameInQueue(Game.Name);

                if (game == null || game == default)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DownloadButton.IsEnabled = true;
                        DownloadProgressRing.Visibility = Visibility.Collapsed;
                        DownloadButtonIcon.Visibility = Visibility.Visible;
                        CancelButton.Visibility = Visibility.Collapsed;
                    });
                    return;
                }
                HasDownloadStarted = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadButton.IsEnabled = false;
                    DownloadButtonText.Text = "Pending...";
                    DownloadProgressRing.Visibility = Visibility.Visible;
                    DownloadProgressRing.IsIndeterminate = true;
                    DownloadButtonIcon.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Visible;
                });

                if (game.Status == ActionStatus.Processing)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HasDownloadStarted = true;
                        DownloadProgressRing.IsIndeterminate = false;
                        DownloadProgressRing.Value = Convert.ToDouble(InstallManager.CurrentInstall.ProgressPercentage);
                        DownloadButtonText.Text = $@"{InstallManager.CurrentInstall.ProgressPercentage} %";
                    });
                    return;
                }

                if (game.Status == ActionStatus.Success && 
                    (game.Action == ActionType.Install ||
                    game.Action == ActionType.Update ||
                    game.Action == ActionType.Repair)
                    )
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DownloadProgressRing.Visibility = Visibility.Collapsed;
                        DownloadButtonIcon.Visibility = Visibility.Visible;
                        DownloadButtonIcon.Glyph = "\uE768";
                        DownloadButtonText.Text = "Play";
                        DownloadButton.IsEnabled= true;
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            InstallManager.CancelInstall(Game.Name);
        }
    }
}
