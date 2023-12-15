using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Crimson.Core;
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
    private ILogger _log = DependencyResolver.Resolve<ILogger>();
    private readonly AuthManager _authManager;
    WindowsSystemDispatcherQueueHelper _mWsdqHelper;
    MicaController _mBackdropController;
    SystemBackdropConfiguration _mConfigurationSource;

    public MainWindow()
    {
        InitializeComponent();

        // Disable setting mica as default
        // We will config later when we do configuration manager
        //TrySetSystemBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _authManager = DependencyResolver.Resolve<AuthManager>();
        _log = DependencyResolver.Resolve<ILogger>();

        IsLoggedIn = false;
        Task.Run(async () =>
        {
            _authManager.AuthStatusChanged += AuthStatusChangedHandler;
            await _authManager.CheckAuthStatus();
        });
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
        //if (args.IsSettingsSelected == true)
        //{
        //    NavView_Navigate(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
        //}
        //else
        if (args.InvokedItemContainer != null)
        {
            var navPageType = Type.GetType(args.InvokedItemContainer.Tag.ToString() ?? string.Empty);
            NavControl_Navigate(navPageType, args.RecommendedNavigationTransitionInfo);
        }
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
    private void UpdateUIBasedOnAuthenticationStatus(AuthenticationStatus authStatus)
    {
        Log.Information($"Auth status: {authStatus}");

        switch (authStatus)
        {
            case AuthenticationStatus.Checking:
                NavControl.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Visible;
                LoginModalTitle.Text = "Checking authentication status";
                LoginModalDescription.Text = "Please wait...";
                break;

            case AuthenticationStatus.LoggedOut:
                NavControl.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Visible;
                LoginPage.InitWebView();
                break;

            case AuthenticationStatus.LoggedIn:
                Log.Information("Logged in");
                LoginModalTitle.Text = "Login Success";

                //LibraryManager.Initialize(_legendaryBinaryPath, Log);
                //_ = LibraryManager.UpdateLibraryAsync();
                //InstallManager.Initialize(_legendaryBinaryPath, Log);

                LoginPage.CloseWebView();

                NavControl.Visibility = Visibility.Visible;
                NavControl.IsEnabled = true;
                LoginPage.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Collapsed;
                NavControl.SelectedItem = NavControl.MenuItems[0];
                NavControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
                Log.Information("Opening Library Page");
                break;

            case AuthenticationStatus.LoginFailed:
                LoginModalTitle.Text = "Login failed";
                LoginModalDescription.Text = "Please try again";
                LoginModal.Visibility = Visibility.Visible;
                NavControl.Visibility = Visibility.Collapsed;
                LoginPage.Visibility = Visibility.Visible;
                break;
        }
    }

    private void AuthStatusChangedHandler(object sender, AuthStatusChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => UpdateUIBasedOnAuthenticationStatus(e.NewStatus));
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
        this.Closed += Window_Closed;
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
        if (_mBackdropController != null)
        {
            _mBackdropController.Dispose();
            _mBackdropController = null;
        }
        this.Activated -= Window_Activated;
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


