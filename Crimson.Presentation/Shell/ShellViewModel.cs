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
        SettingsViewModel settings)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;
        Login = login;
        Library = library;
        Settings = settings;
        _currentPage = login;
        _navigation.Changed += OnNavigationChanged;
    }

    public LoginViewModel Login { get; }

    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }

    public Task ActivateAsync(CancellationToken cancellationToken = default) =>
        Login.ActivateAsync(cancellationToken);

    public void Deactivate()
    {
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
        await _dispatcher.InvokeAsync(() => CurrentPage = route switch
        {
            LoginRoute => Login,
            LibraryRoute => Library,
            GameRoute gameRoute => CreateGameDetails(gameRoute.AppName),
            SettingsRoute => Settings,
            _ => Login
        });
        if (route is LibraryRoute)
            await Library.ActivateAsync();
        else if (route is SettingsRoute)
            await Settings.ActivateAsync();
    }

    private object CreateGameDetails(string appName)
    {
        var game = Library.Games.FirstOrDefault(item => item.AppName == appName);
        return game is null
            ? Library
            : new GameDetailsViewModel(game.AppName, game.Title, game.ImageUri, game.BuildVersion);
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
