using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Crimson.Presentation;

public sealed record GameDetailsViewModel(string AppName, string Title, Uri? ImageUri, string BuildVersion);

public partial class ShellViewModel : ObservableObject, IActivatable, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    [ObservableProperty]
    private object _currentPage;

    public ShellViewModel(
        INavigationService navigation,
        IUiDispatcher dispatcher,
        LoginViewModel login,
        LibraryViewModel library,
        GameInfoViewModel gameInfo,
        DownloadsViewModel downloads,
        SettingsViewModel settings)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;
        Login = login;
        Library = library;
        GameInfo = gameInfo;
        Downloads = downloads;
        Settings = settings;
        _currentPage = login;
        _navigation.Changed += OnNavigationChanged;
    }

    public LoginViewModel Login { get; }

    public LibraryViewModel Library { get; }
    public GameInfoViewModel GameInfo { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    public Task ActivateAsync(CancellationToken cancellationToken = default) =>
        Login.ActivateAsync(cancellationToken);

    public void Deactivate()
    {
        Downloads.Deactivate();
        GameInfo.Deactivate();
        Library.Deactivate();
        Settings.Deactivate();
        Login.Deactivate();
    }

    [RelayCommand]
    private void ShowLibrary()
    {
        if (Login.State == Crimson.Core.EpicAuthenticationState.LoggedIn)
            _navigation.Navigate(new LibraryRoute());
    }

    [RelayCommand]
    private void ShowSettings() => _navigation.Navigate(new SettingsRoute());

    private void OnNavigationChanged(object? sender, AppRoute route) =>
        _ = ApplyRouteAsync(route);

    private async Task ApplyRouteAsync(AppRoute route)
    {
        if (route is not SettingsRoute)
            Settings.Deactivate();
        if (route is not LibraryRoute)
            Library.Deactivate();
        if (route is not GameRoute)
            GameInfo.Deactivate();
        if (route is not DownloadsRoute)
            Downloads.Deactivate();

        object page = route switch
        {
            LoginRoute => Login,
            LibraryRoute => Library,
            GameRoute => GameInfo,
            DownloadsRoute => Downloads,
            SettingsRoute => Settings,
            _ => Login
        };
        await _dispatcher.InvokeAsync(() => CurrentPage = page);
        switch (route)
        {
            case LibraryRoute:
                await Library.ActivateAsync();
                break;
            case GameRoute gameRoute:
                await GameInfo.LoadAsync(gameRoute.AppName);
                break;
            case DownloadsRoute:
                await Downloads.ActivateAsync();
                break;
            case SettingsRoute:
                await Settings.ActivateAsync();
                break;
        }
    }


    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Deactivate();
        _navigation.Changed -= OnNavigationChanged;
    }
}
