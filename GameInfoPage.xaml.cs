using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using WinUiApp.Core;

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
            Game = StateManager.GetGameInfo((string)e.Parameter);
            var gameImage = Game.Images.FirstOrDefault(i => i.Type == "DieselGameBox");
            TitleImage.SetValue(Image.SourceProperty, gameImage != null ? new BitmapImage(new Uri(gameImage.Url)) : null);

            CheckGameStatus(Game);

            // Unregister event handlers on start
            StateManager.GameStatusUpdated -= CheckGameStatus;
            StateManager.GameStatusUpdated += CheckGameStatus;

        }

        private void DownloadButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                PrimaryActionButton.IsEnabled = false;
                PrimaryActionButtonText.Text = "Pending...";
                DownloadProgressRing.Visibility = Visibility.Visible;
                DownloadProgressRing.IsIndeterminate = true;
                PrimaryActionButtonIcon.Visibility = Visibility.Collapsed;
                StateManager.AddToInstallationQueue(Game.Name, ActionType.Install, @"D:\Games\");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                DownloadProgressRing.Visibility = Visibility.Collapsed;
                PrimaryActionButton.IsEnabled = true;
            }
        }

        // Handing Installation State Change
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
                        PrimaryActionButton.IsEnabled = true;
                        DownloadProgressRing.Visibility = Visibility.Collapsed;
                        PrimaryActionButtonIcon.Visibility = Visibility.Visible;
                    });
                    return;
                }
                HasDownloadStarted = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    PrimaryActionButton.IsEnabled = false;
                    PrimaryActionButtonText.Text = "Pending...";
                    DownloadProgressRing.Visibility = Visibility.Visible;
                    DownloadProgressRing.IsIndeterminate = true;
                    PrimaryActionButtonIcon.Visibility = Visibility.Collapsed;
                });

                if (installItem.Status == ActionStatus.Processing)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HasDownloadStarted = true;
                        DownloadProgressRing.IsIndeterminate = false;
                        DownloadProgressRing.Value = Convert.ToDouble(installItem.ProgressPercentage);
                        PrimaryActionButtonText.Text = $@"{installItem.ProgressPercentage} %";
                    });
                    return;
                }

                if (installItem.Status != ActionStatus.Success ||
                    installItem.Action is not (ActionType.Install or ActionType.Update or ActionType.Repair)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadProgressRing.Visibility = Visibility.Collapsed;
                    PrimaryActionButton.Visibility = Visibility.Visible;
                    PrimaryActionButtonText.Text = "Play";
                    PrimaryActionButton.IsEnabled= true;
                });
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

        private void CheckGameStatus(Game updatedGame)
        {
            if (updatedGame == null || updatedGame.Name != Game.Name) return;

            // Unregister the handle instlallation handler for things to work properly
            InstallManager.InstallationStatusChanged -= HandleInstallationStatusChanged;
            Game = updatedGame;

            // Clear ui elements state
            PrimaryActionButtonText.Text = "";
            PrimaryActionButtonIcon.Glyph = "";
            DownloadProgressRing.Visibility = Visibility.Collapsed;

            if (Game.State == Game.InstallState.NotInstalled)
            {
                PrimaryActionButtonText.Text = "Install";
                PrimaryActionButtonIcon.Glyph = "\uE896";
            }
            else if (Game.State == Game.InstallState.Installed)
            {
                PrimaryActionButtonText.Text = "Play";
                PrimaryActionButtonIcon.Glyph = "\uE768";
            }
            else if (Game.State == Game.InstallState.Installing || Game.State == Game.InstallState.Updating || Game.State == Game.InstallState.Repairing)
            {
                // Default button text and glyph if game isn't in instllation queue yet
                PrimaryActionButtonText.Text = "Resume";
                PrimaryActionButtonIcon.Glyph = "\uE768";
                var gameInQueue = InstallManager.GameGameInQueue(Game.Name);
                HandleInstallationStatusChanged(gameInQueue);
                InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;
            }
            else if (Game.State == Game.InstallState.NeedUpdate)
            {
                PrimaryActionButtonText.Text = "Update";
                PrimaryActionButtonIcon.Glyph = "\uE777";
            }
            else if (Game.State == Game.InstallState.Broken)
            {
                PrimaryActionButtonText.Text = "Repair";
                PrimaryActionButtonIcon.Glyph = "\uE90F";
            }
        }

    }
}
