using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinUiApp.StateManager;

namespace WinUiApp;

/// <summary>
///     Main Window
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateManager.StateManager.UpdateLibraryAsync();
    }

    private void navControl_BackRequested(NavigationView sender,
        NavigationViewBackRequestedEventArgs args)
    {
        if (!ContentFrame.CanGoBack)
            return;

        // Don't go back if the nav pane is overlayed.
        if (NavControl.IsPaneOpen &&
            NavControl.DisplayMode is NavigationViewDisplayMode.Compact or NavigationViewDisplayMode.Minimal)
            return;

        ContentFrame.GoBack();
    }

    private void navControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        //if (args.IsSettingsSelected == true)
        //{
        //    NavView_Navigate(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
        //}
        //else
        if (args.InvokedItemContainer != null)
        {
            var navPageType = Type.GetType(args.InvokedItemContainer.Tag.ToString() ?? string.Empty);
            navControl_Navigate(navPageType, args.RecommendedNavigationTransitionInfo);
        }
    }

    private void navControl_Navigate(
        Type navPageType,
        NavigationTransitionInfo transitionInfo)
    {
        // Get the page type before navigation so you can prevent duplicate
        // entries in the backstack.
        var preNavPageType = ContentFrame.CurrentSourcePageType;

        // Only navigate if the selected page isn't currently loaded.
        if (navPageType is not null && !Equals(preNavPageType, navPageType))
            ContentFrame.Navigate(navPageType, null, transitionInfo);
    }

    private void navControl_Loaded(object sender, RoutedEventArgs e)
    {
        NavControl.SelectedItem = NavControl.MenuItems[0];
        navControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
    }

    private void On_Navigated(object sender, NavigationEventArgs e)
    {
        NavControl.IsBackEnabled = ContentFrame.CanGoBack;

        if (ContentFrame.SourcePageType != null)
        {
            // Select the nav view item that corresponds to the page being navigated to.
            NavControl.SelectedItem = NavControl.MenuItems
                .OfType<NavigationViewItem>()
                .First(i => i.Tag.Equals(ContentFrame.SourcePageType.FullName));

            NavControl.Header =
                ((NavigationViewItem)NavControl.SelectedItem)?.Content?.ToString();
        }
    }
}