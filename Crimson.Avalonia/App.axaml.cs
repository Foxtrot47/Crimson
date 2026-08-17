using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Crimson.Infrastructure;
using Crimson.Presentation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Avalonia;

public sealed partial class App : Application
{
    private ShellViewModel? _shell;
    private FileLibraryService? _libraryService;
    private HttpClient? _authenticationClient;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var navigation = new NavigationService();
            var root = AppDataPaths.GetDefaultRoot();
            var window = new MainWindow();
            _libraryService = new FileLibraryService(root);
            var library = new LibraryViewModel(
                _libraryService,
                new AvaloniaUiDispatcher(),
                navigation);
            var settingsService = new FileSettingsService(root);
            var settings = new SettingsViewModel(
                settingsService,
                new AvaloniaFolderPickerService(() => window),
                new DesktopPathLauncher(),
                settingsService.LogsDirectory);
            _authenticationClient = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(15)
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var authentication = new EpicAuthenticationService(
                new InMemoryCredentialStore(),
                _authenticationClient,
                NullLogger<EpicAuthenticationService>.Instance);
            var login = new LoginViewModel(authentication, navigation);
            _shell = new ShellViewModel(navigation, login, library, settings);
            window.DataContext = _shell;
            DataContext = new TrayViewModel(new DesktopApplicationControl(desktop, window));
            window.Opened += OnMainWindowOpened;
            desktop.Exit += OnDesktopExit;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
            window.Opened -= OnMainWindowOpened;
        if (_shell is not null)
            await _shell.ActivateAsync();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _shell?.Dispose();
        _libraryService?.Dispose();
        _libraryService = null;
        _authenticationClient?.Dispose();
        _authenticationClient = null;
        _shell = null;
    }
}
