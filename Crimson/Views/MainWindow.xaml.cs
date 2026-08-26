using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using WinRT;

namespace Crimson.Views;

/// <summary>
///     Main Window
/// </summary>
public sealed partial class MainWindow : Window
{
    // Game rows only. The pane's shared icon size stays near stock so the Library, Downloads
    // and Settings glyphs keep their normal proportions.
    private const int GameIconBoxHeight = 36;

    // Overridden only on the row running an action, so its progress fill survives hover and
    // selection while every other row keeps the normal highlight.
    private static readonly string[] ProgressBackgroundKeys =
    [
        "NavigationViewItemBackground",
        "NavigationViewItemBackgroundPointerOver",
        "NavigationViewItemBackgroundPressed",
        "NavigationViewItemBackgroundSelected",
        "NavigationViewItemBackgroundSelectedPointerOver",
        "NavigationViewItemBackgroundSelectedPressed"
    ];

    // Marks a navigation item that opens a specific game's page.
    private const string GameNavTagPrefix = "game:";

    public bool IsLoggedIn;
    private ILogger _log = App.GetService<ILogger>();
    private readonly AuthManager _authManager;
    private readonly InstallManager _installManager;
    private readonly LibraryManager _libraryManager;

    private List<Game> _libraryCache = new();
    private string? _currentGameAppName;

    // GameStatusUpdated fires several times per install and a rebuild drops the selection, so
    // the menu is only rebuilt when this changes.
    private string _installedMenuSignature = string.Empty;

    private readonly Dictionary<string, GameNavEntry> _gameNavEntries = new(StringComparer.Ordinal);
    private InstallItem? _activeInstall;
    private string? _decoratedAppName;
    private DownloadSummary? _lastDownloadSummary;

    // The parts of one game's pane row that a running action updates.
    private sealed class GameNavEntry
    {
        public required NavigationViewItem Item { get; init; }
        public required string Title { get; init; }
        public required Button PlayButton { get; init; }
        public required StackPanel StatusPanel { get; init; }
        public required FontIcon ActionIcon { get; init; }
        public required TextBlock ProgressText { get; init; }
        public required InfoBadge Badge { get; init; }
        public required LinearGradientBrush? ProgressBrush { get; init; }
        public required bool CanLaunch { get; init; }
    }

    WindowsSystemDispatcherQueueHelper _mWsdqHelper;
    MicaController _mBackdropController;
    SystemBackdropConfiguration _mConfigurationSource;

    public MainWindow()
    {
        InitializeComponent();

        // Disable setting mica as default
        // We will config later when we do configuration manager
        TrySetSystemBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _authManager = App.GetService<AuthManager>();
        _installManager = App.GetService<InstallManager>();
        _libraryManager = App.GetService<LibraryManager>();
        _log = App.GetService<ILogger>();

        _libraryManager.LibraryUpdated += LibraryUpdatedHandler;
        _libraryManager.GameStatusUpdated += GameStatusUpdatedHandler;
        CurrentDownload.SummaryChanged += OnDownloadSummaryChanged;
        _installManager.InstallationStatusChanged += InstallationStatusChangedHandler;
        _installManager.InstallProgressUpdate += InstallProgressUpdateHandler;
        // Badges only show on a collapsed pane, so re-evaluate them whenever it toggles.
        NavControl.PaneOpened += (_, _) => UpdateBadgeVisibility();
        NavControl.PaneClosed += (_, _) => UpdateBadgeVisibility();
        NavControl.DisplayModeChanged += (_, _) => UpdateBadgeVisibility();
        // CurrentDownload published its first summary in its own constructor, before
        // SummaryChanged was attached above.
        CurrentDownload.PublishCurrentSummary();
        // An action may already be running, so seed rather than wait for the next event.
        _activeInstall = IsActionRunning(_installManager.CurrentInstall) ? _installManager.CurrentInstall : null;
        RebuildInstalledMenu();

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
        // Don't go back if the nav pane is overlayed.
        if (NavControl.IsPaneOpen &&
            NavControl.DisplayMode is NavigationViewDisplayMode.Compact or NavigationViewDisplayMode.Minimal)
            return;

        if (ContentFrame.Content is StorePage storePage && storePage.TryGoBack())
            return;

        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void NavControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked == true)
        {
            NavControl_Navigate(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
            return;
        }

        // The original code called Tag.ToString() unguarded, which throws on an item without one.
        if (args.InvokedItemContainer?.Tag is not string tag || tag.Length == 0)
            return;

        if (tag.StartsWith(GameNavTagPrefix, StringComparison.Ordinal))
        {
            NavigateToGame(tag[GameNavTagPrefix.Length..], args.RecommendedNavigationTransitionInfo);
            return;
        }

        NavControl_Navigate(Type.GetType(tag), args.RecommendedNavigationTransitionInfo);
    }

    // Unlike NavControl_Navigate this allows repeat navigation to GameInfoPage: every game is
    // the same page type with a different parameter, so only the game already shown is skipped.
    private void NavigateToGame(string appName, NavigationTransitionInfo transitionInfo)
    {
        if (Equals(ContentFrame.CurrentSourcePageType, typeof(GameInfoPage)) &&
            string.Equals(_currentGameAppName, appName, StringComparison.Ordinal))
            return;

        _currentGameAppName = appName;
        ContentFrame.Navigate(typeof(GameInfoPage), appName, transitionInfo);
    }

    private void LibraryUpdatedHandler(IEnumerable<Game> games)
    {
        if (games == null) return;
        var snapshot = games.ToList();
        DispatcherQueue.TryEnqueue(() =>
        {
            _libraryCache = snapshot;
            RebuildInstalledMenu();
        });
    }

    private void GameStatusUpdatedHandler(Game game)
    {
        if (game == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var index = _libraryCache.FindIndex(cached =>
                string.Equals(cached.AppName, game.AppName, StringComparison.Ordinal));
            if (index >= 0)
                _libraryCache[index] = game;
            else
                _libraryCache.Add(game);

            RebuildInstalledMenu();
        });
    }

    // Rebuilds the installed-game entries that sit directly in the pane. The play button is only
    // offered for states LaunchApp accepts, so a game mid-install is listed but cannot start.
    private void RebuildInstalledMenu()
    {
        var activeAppName = _activeInstall?.AppName;
        var installed = _libraryCache
            .Where(game => game.Metadata is not null && !game.IsDlc())
            .Where(game => IsPaneListed(game, activeAppName))
            .OrderBy(game => game.AppTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Install state and the running action are both in the signature: a row has to be rebuilt
        // when it gains or loses its progress fill, not only when the set of games changes.
        var signature = string.Join("\u001f",
                            installed.Select(game =>
                                $"{game.AppName}\u001e{game.AppTitle}\u001e{game.LocalAppState?.InstallStatus}"))
                        + "\u001d" + activeAppName;
        if (signature == _installedMenuSignature)
            return;
        _installedMenuSignature = signature;
        _gameNavEntries.Clear();

        // Drop only the entries this method owns; the static items keep their position.
        for (var i = NavControl.MenuItems.Count - 1; i >= 0; i--)
        {
            if (NavControl.MenuItems[i] is NavigationViewItem { Tag: string tag } &&
                tag.StartsWith(GameNavTagPrefix, StringComparison.Ordinal))
                NavControl.MenuItems.RemoveAt(i);
        }

        foreach (var game in installed)
            NavControl.MenuItems.Add(CreateInstalledGameItem(game));

        // The rows were just recreated, so re-apply whatever the running action was showing.
        ApplyInstallVisual(_activeInstall);
    }

    // A game earns a row when it is on disk, and also while an action runs against it: a
    // first-time install has to be visible for its progress to be worth reporting.
    private static bool IsPaneListed(Game game, string? activeAppName) =>
        string.Equals(game.AppName, activeAppName, StringComparison.Ordinal) ||
        game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate
            or InstallState.Installing or InstallState.InstallationPaused
            or InstallState.Updating or InstallState.UpdatingPaused
            or InstallState.Repairing;

    private NavigationViewItem CreateInstalledGameItem(Game game)
    {
        var appName = game.AppName;
        var title = string.IsNullOrWhiteSpace(game.AppTitle) ? appName : game.AppTitle;
        var canLaunch = game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate;
        var isActive = string.Equals(appName, _activeInstall?.AppName, StringComparison.Ordinal);

        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(titleBlock, 0);

        // Takes the play button's place while an action runs against this game.
        var actionIcon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var progressText = new TextBlock
        {
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        statusPanel.Children.Add(actionIcon);
        statusPanel.Children.Add(progressText);
        Grid.SetColumn(statusPanel, 1);

        var playButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE768", FontSize = 12 },
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed,
            // A null Background would stop the padded area from hit-testing.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(playButton, $"Play {title}");
        playButton.Click += (_, _) => LaunchGame(appName, title);
        // Without this the tap also reaches the NavigationViewItem and navigates to the game
        // page, so pressing play would both launch and navigate.
        playButton.Tapped += (_, e) => e.Handled = true;
        Grid.SetColumn(playButton, 2);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(titleBlock);
        layout.Children.Add(statusPanel);
        layout.Children.Add(playButton);

        // Only shown on a collapsed pane, where the item template hides the row's content.
        var badge = new InfoBadge { Visibility = Visibility.Collapsed };
        var progressBrush = isActive ? CreateProgressBrush() : null;

        var item = new NavigationViewItem
        {
            Content = layout,
            Tag = GameNavTagPrefix + appName,
            Icon = CreateGameIcon(game),
            InfoBadge = badge
        };
        // One theme resource sizes the icon Viewbox for every row, so raising it on the
        // NavigationView inflates the Library and Settings glyphs too. Scoping the override to
        // this item keeps box art legible without touching them.
        item.Resources["NavigationViewItemOnLeftIconBoxHeight"] = (double)GameIconBoxHeight;
        if (progressBrush is not null)
        {
            item.Background = progressBrush;
            foreach (var key in ProgressBackgroundKeys)
                item.Resources[key] = progressBrush;
        }

        ToolTipService.SetToolTip(item, title);

        _gameNavEntries[appName] = new GameNavEntry
        {
            Item = item,
            Title = title,
            PlayButton = playButton,
            StatusPanel = statusPanel,
            ActionIcon = actionIcon,
            ProgressText = progressText,
            Badge = badge,
            ProgressBrush = progressBrush,
            CanLaunch = canLaunch
        };
        return item;
    }

    // The set of rows can change here: an install that just started needs one, and a finished
    // uninstall no longer qualifies for one.
    private void InstallationStatusChangedHandler(InstallItem? installItem)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _activeInstall = IsActionRunning(installItem) ? installItem : null;
            RebuildInstalledMenu();
            ApplyInstallVisual(_activeInstall);
        });
    }

    // Never rebuilds the menu: ticks arrive many times a second and recreating the rows would
    // refetch every icon.
    private void InstallProgressUpdateHandler(InstallItem? installItem)
    {
        if (installItem is null) return;
        DispatcherQueue.TryEnqueue(() => ApplyInstallVisual(installItem));
    }

    private static bool IsActionRunning(InstallItem? installItem) =>
        installItem?.Status is ActionStatus.Pending or ActionStatus.OnGoing or ActionStatus.Processing
            or ActionStatus.Paused or ActionStatus.Cancelling;

    // Pushes an action's glyph, percentage and fill onto the matching row, and returns every
    // other row to its idle state.
    private void ApplyInstallVisual(InstallItem? installItem)
    {
        var activeAppName = IsActionRunning(installItem) ? installItem!.AppName : null;

        // Only disturb rows when the decorated row actually changes; a rebuild already recreates
        // every row idle.
        if (!string.Equals(_decoratedAppName, activeAppName, StringComparison.Ordinal))
        {
            if (_decoratedAppName is not null &&
                _gameNavEntries.TryGetValue(_decoratedAppName, out var previous))
                ResetEntry(previous);
            _decoratedAppName = activeAppName;
        }

        if (activeAppName is null || !_gameNavEntries.TryGetValue(activeAppName, out var target))
            return;

        var percent = Math.Clamp(installItem!.ProgressPercentage, 0, 100);
        var label = CurrentDownloadControl.GetActionLabel(installItem.Action);

        target.PlayButton.Visibility = Visibility.Collapsed;
        target.StatusPanel.Visibility = Visibility.Visible;
        target.ActionIcon.Glyph = GetActionGlyph(installItem.Action, installItem.Status);
        target.ProgressText.Text = $"{percent}%";

        target.Badge.Value = percent;
        UpdateBadgeVisibility();

        SetProgressFill(target.ProgressBrush, percent);
        ToolTipService.SetToolTip(target.Item,
            installItem.Status == ActionStatus.Paused
                ? $"{target.Title} \u2014 {label} paused at {percent}%"
                : $"{target.Title} \u2014 {label} {percent}%");
    }

    private static void ResetEntry(GameNavEntry entry)
    {
        entry.StatusPanel.Visibility = Visibility.Collapsed;
        entry.PlayButton.Visibility = entry.CanLaunch ? Visibility.Visible : Visibility.Collapsed;
        entry.Badge.Visibility = Visibility.Collapsed;
        SetProgressFill(entry.ProgressBrush, 0);
        ToolTipService.SetToolTip(entry.Item, entry.Title);
    }

    // Segoe MDL2 glyph naming the action, or the reason it is not currently advancing.
    private static string GetActionGlyph(ActionType action, ActionStatus status) => status switch
    {
        ActionStatus.Paused => "\uE769",
        ActionStatus.Cancelling => "\uE711",
        _ => action switch
        {
            ActionType.Install => "\uE896",
            ActionType.Update => "\uE895",
            ActionType.Repair or ActionType.Verify => "\uE90F",
            ActionType.Uninstall => "\uE74D",
            ActionType.Move => "\uE8DE",
            ActionType.Import => "\uE8B5",
            _ => "\uE896"
        }
    };

    // Left-to-right fill used as the row background, in the colour the pane already uses for
    // hover and selection. The two middle stops share an offset, so the fill ends on a hard edge
    // at the progress point.
    private static LinearGradientBrush CreateProgressBrush()
    {
        var fill = GetSelectionFillColor();
        // Double alpha at the far left leaves a hint of gradient without a second colour.
        var lead = Windows.UI.Color.FromArgb((byte)Math.Min(255, fill.A * 2), fill.R, fill.G, fill.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = lead });
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = fill });
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Microsoft.UI.Colors.Transparent });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Microsoft.UI.Colors.Transparent });
        return brush;
    }

    // Colour the item presenter uses for its hover and selected states. The literal is the
    // dark-theme value, reached only if the theme resource cannot be resolved.
    private static Windows.UI.Color GetSelectionFillColor() =>
        Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var value) &&
        value is SolidColorBrush brush
            ? brush.Color
            : Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);

    // Moves the fill edge. Mutating the live brush keeps the row's style intact, which replacing
    // the brush would not.
    private static void SetProgressFill(LinearGradientBrush? brush, int percent)
    {
        if (brush is null) return;
        var offset = Math.Clamp(percent / 100d, 0d, 1d);
        brush.GradientStops[1].Offset = offset;
        brush.GradientStops[2].Offset = offset;
    }

    // Builds the pane icon for a game, or null when it has no usable artwork, which leaves the
    // item without an icon rather than showing a broken one.
    private static IconElement? CreateGameIcon(Game game)
    {
        var keyImages = game.Metadata?.KeyImages;
        if (keyImages is null) return null;

        // DieselGameBoxTall is the one image every record in the library carries.
        // DieselGameBoxLogo covers well under a fifth of them, and mixing wide transparent
        // wordmarks into a column of 3:4 box art makes the pane less uniform, not more.
        var url = keyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
                  ?? keyImages.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;

        if (string.IsNullOrEmpty(url)) return null;

        // The source art is 1200x1600. Decoding that for a row icon wastes memory and resamples
        // poorly; the decode hint has to be set before UriSource kicks off the download.
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelHeight = GameIconBoxHeight * 2
        };
        bitmap.UriSource = new Uri(url);

        // The square box gives every row the same icon footprint; ImageIcon fits the art
        // Uniform inside it, so portrait and landscape sources still line up.
        return new ImageIcon { Source = bitmap, Width = GameIconBoxHeight, Height = GameIconBoxHeight };
    }

    private void LaunchGame(string appName, string title)
    {
        _log.Information("MainWindow: Launching {Title} ({AppName}) from the navigation pane", title, appName);
        // LaunchApp blocks on Process.WaitForExit for the lifetime of the game, so it must not
        // run on the UI thread. It logs its own failures and does not throw.
        _ = Task.Run(() => _libraryManager.LaunchApp(appName));
    }

    private void OnDownloadSummaryChanged(DownloadSummary summary)
    {
        _lastDownloadSummary = summary;
        ToolTipService.SetToolTip(DownloadsNavItem, summary.ToolTip);
        DownloadInfoBadge.Value = summary.ProgressPercentage;
        UpdateBadgeVisibility();
    }

    // While the pane is open both badges only repeat what is already legible beside them: the
    // game row spells out its percentage and the footer draws a full progress bar.
    private void UpdateBadgeVisibility()
    {
        var collapsed = !NavControl.IsPaneOpen;

        DownloadInfoBadge.Visibility = collapsed && _lastDownloadSummary is { HasActiveDownload: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_decoratedAppName is not null && _gameNavEntries.TryGetValue(_decoratedAppName, out var entry))
            entry.Badge.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
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

                LoginPage.CloseWebView();

                NavControl.Visibility = Visibility.Visible;
                NavControl.IsEnabled = true;
                LoginPage.Visibility = Visibility.Collapsed;
                LoginModal.Visibility = Visibility.Collapsed;
                NavControl.SelectedItem = NavControl.MenuItems[0];
                NavControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
                Log.Information("Opening Library Page");

                _installManager.LoadPendingInstalls();

                // GetLibraryData serves a cached list for 20 minutes without raising
                // LibraryUpdated, so the pane has to seed itself rather than wait for the event.
                Task.Run(async () =>
                {
                    try
                    {
                        LibraryUpdatedHandler(await _libraryManager.GetLibraryData());
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "MainWindow: could not load the installed games list");
                    }
                });
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

        // LibraryManager is a singleton and outlives this window.
        _libraryManager.LibraryUpdated -= LibraryUpdatedHandler;
        _libraryManager.GameStatusUpdated -= GameStatusUpdatedHandler;
        CurrentDownload.SummaryChanged -= OnDownloadSummaryChanged;
        _installManager.InstallationStatusChanged -= InstallationStatusChangedHandler;
        _installManager.InstallProgressUpdate -= InstallProgressUpdateHandler;
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


