// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

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
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUiApp
{
    /// <summary>
    /// Library Page which shows list of currently installed games
    /// </summary>
    public sealed partial class LibraryPage : Page
    {
        public static ObservableCollection<GameItem> GamesList { get; } = new ObservableCollection<GameItem>();
        public static bool LoadingFinished = false;

        public LibraryPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (LoadingFinished)
                return;
            LoadingSection.Visibility = Visibility.Visible;
             FetchGameLibraryAsync();
        }

        private async Task FetchGameLibraryAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    var lib = new Legendary.Library("D:\\Software\\Projects\\WinUiApp\\Binaries\\legendary.exe");
                    var json = lib.FetchGamesList();
                    foreach (var game in json.EnumerateArray())
                    {
                        Console.WriteLine(game);
                        var appName = game.GetProperty("metadata").GetProperty("title").GetString();

                        // Get the keyImages
                        var keyImages = game
                            .GetProperty("metadata")
                            .GetProperty("keyImages")
                            .EnumerateArray();

                        var image = new GameImage();
                        foreach (var keyImage in keyImages)
                        {
                            // we are taking image with resolution 1200 x 1600 for proper cropping
                            if (keyImage.GetProperty("type").GetString() == "DieselGameBoxTall")
                            {
                                // Pass height and width to url to get cropped image
                                image.Url = keyImage.GetProperty("url").GetString() + "?h=400&resize=1&w=300";
                                image.Width = keyImage.GetProperty("width").GetInt32();
                                image.Height = keyImage.GetProperty("height").GetInt32();
                                break;
                            }
                        }

                        var gameItem = new GameItem { Name = appName, GameImage = image };
                        DispatcherQueue.TryEnqueue(() => GamesList.Add(gameItem)); // Update the GamesList on the UI thread
                        DispatcherQueue.TryEnqueue(() => LoadingSection.Visibility = Visibility.Collapsed);
                        LoadingFinished = true;
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            Button clickedButton = (Button)sender;
            GameItem gameItem = (GameItem)clickedButton.DataContext;

            Frame navControl = FindParentFrame(this);

            if (navControl == null)
                return;

            string param = JsonSerializer.Serialize(gameItem);

            navControl.Navigate(typeof(GameInfo), param);
        }

        private Frame FindParentFrame(DependencyObject child)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);

            while (parent != null && !(parent is Frame))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            return parent as Frame;
        }


    }
}
