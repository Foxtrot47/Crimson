// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ABI.System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Storage.Pickers;
using WinRT;
using WinUiApp.Legendary;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUiApp;

/// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    public ObservableCollection<GameItem> GamesList { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        FetchGameLibrary();
    }

    private void FetchGameLibrary()
    {
        var lib = new Library("D:\\Software\\Projects\\WinUiApp\\Binaries\\legendary.exe");
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
            GamesList.Add(gameItem);
        }
    }
}

public class GameItem
{
    public string Name { get; set; }
    public GameImage GameImage { get; set; }
}

public class GameImage
{
    public string Url { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}