using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace Crimson
{
    /// <summary>
    /// Library Page which shows list of currently installed games
    /// </summary>
    public sealed partial class LibraryPage : Page
    {
        public static List<LibraryItem> GamesList { get; set; }
        public bool LoadingFinished = false;

        // Get logger instance from MainWindow window class
        private readonly ILogger _log;
        private readonly LibraryManager _libraryManager;

        public LibraryPage()
        {
            InitializeComponent();

            _log = DependencyResolver.Resolve<ILogger>();
            _libraryManager = DependencyResolver.Resolve<LibraryManager>();
            _log.Information("LibraryPage: Loading Page");

            LoadingSection.Visibility = Visibility.Visible;
            GamesGrid.Visibility = Visibility.Collapsed;

            Task.Run(async () =>
            {
                var games = await _libraryManager.GetLibraryData();
                UpdateLibrary(games);
            });

            DataContext = this;
            _libraryManager.LibraryUpdated += UpdateLibrary;
            _log.Information("LibraryPage: Loading finished");
        }

        private void UpdateLibrary(IEnumerable<Game> games)
        {
            try
            {
                _log.Information("UpdateLibrary: Updating Library Page");
                if (games == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    GamesList = new List<LibraryItem>();
                    foreach (var game in games)
                    {
                        if (game.IsDlc()) continue;
                        var item = new LibraryItem
                        {
                            Name = game.AppName,
                            Title = game.AppTitle,
                            //InstallState = game.State,
                            Image = Util.GetBitmapImage(game.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
                        };
                        _log.Information($"UpdateLibrary: Adding {item.Name} to Library");
                        GamesList.Add(item);
                    }
                    GamesList = GamesList.OrderBy(item => item.Title).ToList();
                    ItemsRepeater.ItemsSource = GamesList;
                    LoadingSection.Visibility = Visibility.Collapsed;
                    GamesGrid.Visibility = Visibility.Visible;
                });
                _log.Information("UpdateLibrary: Updated Library Page");
            }
            catch (Exception ex)
            {
                _log.Error(ex.ToString());
            }
        }

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            var clickedButton = (Button)sender;
            var game = (LibraryItem)clickedButton.DataContext;
            var navControl = FindParentFrame(this);

            if (navControl == null)
                return;

            navControl.Navigate(typeof(GameInfoPage), game.Name);
        }

        private static Frame FindParentFrame(DependencyObject child)
        {
            var parent = VisualTreeHelper.GetParent(child);

            while (parent != null && parent is not Microsoft.UI.Xaml.Controls.Frame)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as Frame;
        }
    }

    public class LibraryItem
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public BitmapImage Image { get; set; }
        //public Game.InstallState InstallState { get; set; }
    }
}
