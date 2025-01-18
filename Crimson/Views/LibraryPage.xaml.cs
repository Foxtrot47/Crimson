﻿using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace Crimson.Views;

/// <summary>
/// Library Page which shows list of currently installed games
/// </summary>
public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    public LibraryPage()
    {
        InitializeComponent();
        DataContext = App.GetService<LibraryViewModel>();
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
