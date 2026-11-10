using Crimson.Providers;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using System;
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
        NavigationCacheMode = NavigationCacheMode.Enabled;
        DataContext = App.GetService<LibraryViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        BuildLibraryActions();
        await ViewModel.OnNavigatedTo(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    /// <summary>Buttons come from the providers themselves; the shell never names one.</summary>
    private void BuildLibraryActions()
    {
        LibraryActionsBar.Children.Clear();
        foreach (var frontend in App.GetService<LauncherFrontendRegistry>().All)
        {
            foreach (var action in frontend.LibraryActions)
            {
                var button = new Button { Height = 36, Tag = action };
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                content.Children.Add(new FontIcon { FontSize = 16, Glyph = action.Glyph });
                content.Children.Add(new TextBlock { Text = action.Label });
                button.Content = content;
                button.Click += async (sender, _) =>
                {
                    if (sender is Button { Tag: LauncherLibraryAction selected }) await selected.Invoke();
                };
                LibraryActionsBar.Children.Add(button);
            }
        }
        LibraryActionsBar.Visibility = LibraryActionsBar.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void ManageAccounts_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));

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
