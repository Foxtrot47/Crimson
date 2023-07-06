using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinUiApp.Core;
using WinRT;
using System.Threading.Tasks;
using static WinUiApp.Core.Legendary;
using static System.Net.WebRequestMethods;
using Windows.Storage;

namespace WinUiApp;

/// <summary>
///     Main Window
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string LegendaryBinaryPathFormat = @"C:\Users\{0}\AppData\Local\WinUIEGL\bin\legendary.exe";

    public bool IsLoggedIn;
    public string legendaryBinaryPath;

    public MainWindow()
    {
        InitializeComponent();
        IsLoggedIn = false;
        Task.Run(async () =>
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var res = await Legendary.DownloadBinaryAsync(localFolder);
            legendaryBinaryPath = res.Path;
            var legendaryInstance = new Legendary(legendaryBinaryPath);
            legendaryInstance.CheckAuthentication();
            legendaryInstance.AuthenticationStatusChanged += HandleAuthenticationChanges;
        });

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

    private void UpdateUIBasedOnAuthenticationStatus(AuthenticationStatus authStatus)
    {
        LoginModal.Visibility = Visibility.Visible;

        switch (authStatus)
        {
            case AuthenticationStatus.Checking:
                LoginModalTitle.Text = "Logging in to Epic Games Store";
                LoginModalDescription.Text = "Please wait...";
                break;

            case AuthenticationStatus.LoginWindowOpen:
                LoginModalTitle.Text = "Logging in to Epic Games Store";
                LoginModalDescription.Text = "Please switch to the opened window";
                break;

            case AuthenticationStatus.LoggedIn:
                LoginModalTitle.Text = "Login Success";
                LoginModalDescription.Text = "Please wait...";
                _ = StateManager.UpdateLibraryAsync();
                LoginModal.Visibility = Visibility.Collapsed;
                NavControl.SelectedItem = NavControl.MenuItems[0];
                navControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
                break;

            case AuthenticationStatus.LoginFailed:
                LoginModalTitle.Text = "Login failed";
                LoginModalDescription.Text = "Please try again";
                break;
        }
    }

    private void HandleAuthenticationChanges(AuthenticationStatus authStatus)
    {
        DispatcherQueue.TryEnqueue(() => UpdateUIBasedOnAuthenticationStatus(authStatus));
    }
}


