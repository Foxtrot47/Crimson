using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUiApp.Core;

namespace WinUiApp
{
    /// <summary>
    /// Library Page which shows list of currently installed games
    /// </summary>
    public sealed partial class LibraryPage : Page
    {
        public static ObservableCollection<LibraryItem> GamesList { get; set; }
        public bool LoadingFinished = false;

        public LibraryPage()
        {
            InitializeComponent();
            LoadingSection.Visibility = Visibility.Visible;
            GamesGrid.Visibility = Visibility.Collapsed;
            DataContext = this;
            StateManager.LibraryUpdated += UpdateLibrary;
            if (!LoadingFinished)
            {
                GamesList = new ObservableCollection<LibraryItem>();
                try
                {
                    var data = StateManager.GetLibraryData();
                    if (data == null) return;
                    UpdateLibrary(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            LoadingFinished = true;
        }

        private void UpdateLibrary(ObservableCollection<Game> games)
        {
            try
            {
                if (games == null) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    GamesList = new ObservableCollection<LibraryItem>();
                    foreach (var game in games)
                    {
                        var item = new LibraryItem
                        {
                            Name = game.Name,
                            Title = game.Title,
                            InstallState = game.State,
                            Image = Util.GetBitmapImage(game.Images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
                        };
                        GamesList.Add(item);
                    }
                    ItemsRepeater.ItemsSource = GamesList;
                    LoadingSection.Visibility = Visibility.Collapsed;
                    GamesGrid.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                Console.Write(ex.ToString());
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
        public Game.InstallState InstallState { get; set; }
    }
}
