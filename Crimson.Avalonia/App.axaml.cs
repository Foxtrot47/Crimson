using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Presentation;
using Crimson.Repository;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Avalonia;

public sealed partial class App : Application
{
    private ShellViewModel? _shell;
    private LibraryService? _libraryService;
    private HttpClient? _authenticationClient;
    private HttpClient? _apiClient;
    private HttpClient? _contentClient;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var navigation = new NavigationService();
            var root = AppDataPaths.GetDefaultRoot();
            var window = new MainWindow();
            var settingsService = new FileSettingsService(root);
            var folderPicker = new AvaloniaFolderPickerService(() => window);
            var dispatcher = new AvaloniaUiDispatcher();
            var settings = new SettingsViewModel(
                settingsService,
                folderPicker,
                new DesktopPathLauncher(),
                settingsService.LogsDirectory);
            _authenticationClient = CreateClient(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(5));
            _apiClient = CreateClient(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(10));
            _contentClient = CreateClient(
                TimeSpan.FromMinutes(10),
                TimeSpan.FromSeconds(15));
            var authentication = new EpicAuthenticationService(
                new InMemoryCredentialStore(),
                _authenticationClient,
                NullLogger<EpicAuthenticationService>.Instance);
            var repository = new EpicGamesRepository(
                authentication,
                NullLogger<EpicGamesRepository>.Instance,
                _apiClient,
                _contentClient);
            _libraryService = new LibraryService(repository, new FileLibraryStore(root));
            var library = new LibraryViewModel(_libraryService, dispatcher, navigation);
            var login = new LoginViewModel(authentication, navigation);
            var gameWorkflow = new ReadOnlyGameWorkflowService(_libraryService);
            var gameInfo = new GameInfoViewModel(
                gameWorkflow,
                new UnsupportedInstallDialogService(),
                folderPicker,
                dispatcher);
            var currentOperation = new CurrentOperationViewModel(gameWorkflow, dispatcher);
            var downloads = new DownloadsViewModel(gameWorkflow, dispatcher, currentOperation);
            _shell = new ShellViewModel(
                navigation,
                dispatcher,
                login,
                library,
                gameInfo,
                downloads,
                settings);
            window.DataContext = _shell;
            DataContext = new TrayViewModel(new DesktopApplicationControl(desktop, window));
            window.Opened += OnMainWindowOpened;
            desktop.Exit += OnDesktopExit;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static HttpClient CreateClient(TimeSpan timeout, TimeSpan connectTimeout)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 16,
            ConnectTimeout = connectTimeout
        })
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(EpicLauncherWebLogin.ApiUserAgent);
        return client;
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
        _apiClient?.Dispose();
        _apiClient = null;
        _contentClient?.Dispose();
        _contentClient = null;
        _shell = null;
    }
}
