using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;
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
            InstallManager.InstallationStatusChanged -= HandleInstallationStatusChanged;
            InstallManager.InstallationStatusChanged += HandleInstallationStatusChanged;
            InstallManager.InstallProgressUpdate -= HandleInstallationStatusChanged;
            InstallManager.InstallProgressUpdate += HandleInstallationStatusChanged;

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
                if (installItem == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (installItem.Status)
                    {
                        case ActionStatus.Processing:
                            DownloadProgressRing.IsIndeterminate = false;
                            DownloadProgressRing.Value = Convert.ToDouble(installItem.ProgressPercentage);
                            DownloadProgressRing.Visibility = Visibility.Visible;
                            PrimaryActionButtonIcon.Visibility = Visibility.Collapsed;
                            PrimaryActionButton.IsEnabled = false;
                            PrimaryActionButtonText.Text = $"{installItem.ProgressPercentage}%";
                            break;

                        case ActionStatus.Pending:

                            PrimaryActionButtonText.Text = "Pending...";
                            DownloadProgressRing.Visibility = Visibility.Visible;
                            DownloadProgressRing.IsIndeterminate = true;
                            PrimaryActionButtonIcon.Visibility = Visibility.Collapsed;

                            break;
                        case ActionStatus.Cancelling:
                            PrimaryActionButtonText.Text = "Cancelling...";
                            DownloadProgressRing.Visibility = Visibility.Visible;
                            DownloadProgressRing.IsIndeterminate = true;
                            PrimaryActionButtonIcon.Visibility = Visibility.Collapsed;
                            break;
                    }
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

            Game = updatedGame;

            DispatcherQueue.TryEnqueue(() =>
            {
                // Clear ui elements state
                PrimaryActionButtonText.Text = "";
                PrimaryActionButtonIcon.Glyph = "";

                if (Game.State == Game.InstallState.Installing || Game.State == Game.InstallState.Updating || Game.State == Game.InstallState.Repairing)
                {
                    var gameInQueue = InstallManager.GameGameInQueue(Game.Name);
                    if (gameInQueue == null)
                    {
                        // Default button text and glyph if game isn't in instllation queue yet
                        PrimaryActionButtonText.Text = "Resume";
                        PrimaryActionButtonIcon.Glyph = "\uE768";
                    }
                    HandleInstallationStatusChanged(gameInQueue);
                    return;
                }
                PrimaryActionButtonIcon.Visibility = Visibility.Visible;
                DownloadProgressRing.Visibility = Visibility.Collapsed;
                PrimaryActionButton.IsEnabled = true;
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
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Unregister both event handlers before navigating out
            StateManager.GameStatusUpdated -= CheckGameStatus;
            InstallManager.InstallationStatusChanged -= HandleInstallationStatusChanged;
            InstallManager.InstallProgressUpdate -= HandleInstallationStatusChanged;

            // Call the base implementation
            base.OnNavigatedFrom(e);
        }
    }
}
