using System.Linq;
using Crimson.Providers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>Hosts one provider's storefront. The shell routes a destination here; browsing stays with the provider.</summary>
public sealed partial class StorePage : Page
{
    private readonly Frame _providerFrame = new();

    public StorePage()
    {
        InitializeComponent();
        Content = _providerFrame;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is StoreDestination destination) Open(destination);
        else if (Home() is { } home) Open(home);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _providerFrame.Navigate(typeof(Page));
        _providerFrame.BackStack.Clear();
        base.OnNavigatedFrom(e);
    }

    public bool TryGoBack()
    {
        if (_providerFrame.Content is ILauncherStoreView view && view.TryGoBack()) return true;
        if (_providerFrame.CanGoBack) { _providerFrame.GoBack(); return true; }
        return false;
    }

    public void Open(StoreDestination destination)
    {
        var registry = App.GetService<LauncherRegistry>();
        if (registry.Get(destination.ProviderId).Store is null) return;
        var pageType = App.GetService<LauncherFrontendRegistry>().Get(destination.ProviderId).StorePageType;
        if (pageType is null) return;
        _providerFrame.Navigate(pageType, destination.Uri);
    }

    /// <summary>The storefront a bare Store click opens: the first store that serves one.</summary>
    private static StoreDestination? Home()
    {
        var frontends = App.GetService<LauncherFrontendRegistry>();
        var provider = App.GetService<LauncherStores>().Providers
            .FirstOrDefault(candidate => frontends.Get(candidate.Id).StorePageType is not null);
        return provider is null ? null : new StoreDestination(provider.Id, provider.Store!.HomeUri);
    }
}
