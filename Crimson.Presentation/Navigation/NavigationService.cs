namespace Crimson.Presentation;

public abstract record AppRoute;

public sealed record LoginRoute : AppRoute;

public sealed record LibraryRoute : AppRoute;

public sealed record GameRoute(string AppName) : AppRoute;

public sealed record DownloadsRoute : AppRoute;

public sealed record SettingsRoute : AppRoute;

public interface INavigationService
{
    AppRoute Current { get; }

    event EventHandler<AppRoute>? Changed;

    void Navigate(AppRoute route);
}

public sealed class NavigationService : INavigationService
{
    public AppRoute Current { get; private set; } = new LoginRoute();

    public event EventHandler<AppRoute>? Changed;

    public void Navigate(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route == Current)
            return;
        Current = route;
        Changed?.Invoke(this, route);
    }
}
