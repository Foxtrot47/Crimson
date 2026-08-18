using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Presentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;
using WinRT;

namespace Crimson.Views;

/// <summary>
///     Main Window
/// </summary>
public sealed partial class MainWindow : Window
{
    public bool IsLoggedIn;
    private readonly ILogger _log;
    private readonly InstallManager _installManager;
    private readonly ShellViewModel _shell;
    private readonly LoginViewModel _login;
    private readonly INavigationService _navigation;

    WindowsSystemDispatcherQueueHelper _mWsdqHelper;
    MicaController _mBackdropController;
    SystemBackdropConfiguration _mConfigurationSource;

    public MainWindow()
    {
        InitializeComponent();
        Closed += Window_Closed;

        // Disable setting mica as default
        // We will config later when we do configuration manager
        TrySetSystemBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _installManager = App.GetService<InstallManager>();
        _shell = App.GetService<ShellViewModel>();
        _login = App.GetService<LoginViewModel>();
        _navigation = App.GetService<INavigationService>();
        _log = App.GetService<ILogger>();
        _login.PropertyChanged += OnLoginPropertyChanged;
        _navigation.Changed += OnNavigationChanged;
        IsLoggedIn = false;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _shell.ActivateAsync();
            UpdateUIBasedOnAuthenticationStatus(_login.State);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Main window activation failed");
            UpdateUIBasedOnAuthenticationStatus(EpicAuthenticationState.Failed);
        }
    }

    private void NavControl_BackRequested(NavigationView sender,
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

    private void NavControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _navigation.Navigate(new SettingsRoute());
            return;
        }

        var tag = args.InvokedItemContainer?.Tag?.ToString();
        if (tag == "Crimson.Views.LibraryPage")
            _navigation.Navigate(new LibraryRoute());
        else if (tag == "Crimson.Views.DownloadsPage")
            _navigation.Navigate(new DownloadsRoute());
    }

    private void NavControl_Navigate(
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
    private void UpdateUIBasedOnAuthenticationStatus(EpicAuthenticationState authStatus)
    {
        _log.Information("Auth status: {Status}", authStatus);
        switch (authStatus)
        {
            case EpicAuthenticationState.Checking:
            case EpicAuthenticationState.Authenticating:
                NavControl.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Visible;
                LoginModalTitle.Text = "Checking authentication status";
                LoginModalDescription.Text = "Please wait...";
                break;
            case EpicAuthenticationState.LoggedOut:
                NavControl.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Visible;
                _ = LoginPage.InitWebViewAsync();
                break;
            case EpicAuthenticationState.LoggedIn:
                LoginModalTitle.Text = "Login Success";
                LoginPage.CloseWebView();
                NavControl.Visibility = Visibility.Visible;
                NavControl.IsEnabled = true;
                LoginPage.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Collapsed;
                NavControl.SelectedItem = NavControl.MenuItems[0];
                _navigation.Navigate(new LibraryRoute());
                _ = _installManager.LoadPendingInstalls();
                break;
            case EpicAuthenticationState.Failed:
                LoginModalTitle.Text = "Login failed";
                LoginModalDescription.Text = "Please try again";
                LoginModal.Visibility = Visibility.Visible;
                NavControl.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnLoginPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LoginViewModel.State))
            DispatcherQueue.TryEnqueue(() => UpdateUIBasedOnAuthenticationStatus(_login.State));
    }

    private void OnNavigationChanged(object? sender, AppRoute route)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (route)
            {
                case LibraryRoute:
                    NavControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
                    break;
                case GameRoute game:
                    ContentFrame.Navigate(typeof(GameInfoPage), game.AppName, new EntranceNavigationTransitionInfo());
                    break;
                case DownloadsRoute:
                    NavControl_Navigate(typeof(DownloadsPage), new EntranceNavigationTransitionInfo());
                    break;
                case SettingsRoute:
                    NavControl_Navigate(typeof(SettingsPage), new EntranceNavigationTransitionInfo());
                    break;
            }
        });
    }
    private bool TrySetSystemBackdrop()
    {
        if (!MicaController.IsSupported())
            return false; // Mica is not supported on this system
        _mWsdqHelper = new WindowsSystemDispatcherQueueHelper();
        _mWsdqHelper.EnsureWindowsSystemDispatcherQueueController();

        // Create the policy object.
        _mConfigurationSource = new SystemBackdropConfiguration();
        this.Activated += Window_Activated;
        ((FrameworkElement)this.Content).ActualThemeChanged += Window_ThemeChanged;

        // Initial configuration state.
        _mConfigurationSource.IsInputActive = true;
        SetConfigurationSourceTheme();

        _mBackdropController = new MicaController();

        // Enable the system backdrop.
        // Note: Be sure to have "using WinRT;" to support the Window.As<...>() call.
        _mBackdropController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _mBackdropController.SetSystemBackdropConfiguration(_mConfigurationSource);
        return true; // succeeded

    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _mConfigurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        // Make sure any Mica/Acrylic controller is disposed
        // so it doesn't try to use this closed window.
        Closed -= Window_Closed;
        if (_mBackdropController != null)
        {
            _mBackdropController.Dispose();
            _mBackdropController = null;
        }
        this.Activated -= Window_Activated;
        _login.PropertyChanged -= OnLoginPropertyChanged;
        _navigation.Changed -= OnNavigationChanged;
        _shell.Dispose();
        _mConfigurationSource = null;
    }

    private void Window_ThemeChanged(FrameworkElement sender, object args)
    {
        if (_mConfigurationSource != null)
        {
            SetConfigurationSourceTheme();
        }
    }

    private void SetConfigurationSourceTheme()
    {
        switch (((FrameworkElement)this.Content).ActualTheme)
        {
            case ElementTheme.Dark: _mConfigurationSource.Theme = SystemBackdropTheme.Dark; break;
            case ElementTheme.Light: _mConfigurationSource.Theme = SystemBackdropTheme.Light; break;
            case ElementTheme.Default: _mConfigurationSource.Theme = SystemBackdropTheme.Default; break;
        }
    }
}

internal class WindowsSystemDispatcherQueueHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object dispatcherQueueController);

    private object _mDispatcherQueueController = null;
    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null)
        {
            // one already exists, so we'll just use it.
            return;
        }

        if (_mDispatcherQueueController != null) return;
        DispatcherQueueOptions options;
        options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
        options.threadType = 2;    // DQTYPE_THREAD_CURRENT
        options.apartmentType = 2; // DQTAT_COM_STA

        CreateDispatcherQueueController(options, ref _mDispatcherQueueController);
    }
}


