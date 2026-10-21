using System;
using Crimson.Providers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;

namespace Crimson.Views;

/// <summary>All stores share one destination; browsing never changes the library or account context.</summary>
public sealed class StorePage : Page
{
    private readonly Frame _providerFrame = new();
    private readonly StackPanel _hub = new() { Margin = new Thickness(36), Spacing = 16 };
    private readonly Grid _root = new();

    public StorePage()
    {
        _root.Children.Add(_hub);
        _root.Children.Add(_providerFrame);
        Content = _root;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _providerFrame.Visibility = Visibility.Collapsed;
        _hub.Visibility = Visibility.Visible;
        var query = e.Parameter as string;
        _hub.Children.Clear();
        _hub.Children.Add(new TextBlock
        {
            Text = query is null ? "Stores" : $"Search stores for “{query}”",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"]
        });
        foreach (var provider in App.GetService<LauncherStores>().Providers)
        {
            var store = provider.Store!;
            var target = new StoreDestination(provider.Id, query is null ? store.HomeUri : store.CreateSearchUri(query));
            var button = new Button { Content = store.DisplayName };
            button.Click += (_, _) => Open(target);
            _hub.Children.Add(button);
        }
        if (e.Parameter is StoreDestination destination) Open(destination);
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
        if (_providerFrame.Visibility == Visibility.Visible)
        {
            _providerFrame.Navigate(typeof(Page));
            _providerFrame.Visibility = Visibility.Collapsed;
            _hub.Visibility = Visibility.Visible;
            return true;
        }
        return false;
    }

    public void Open(StoreDestination destination)
    {
        var registry = App.GetService<LauncherRegistry>();
        if (registry.Get(destination.ProviderId).Store is null) return;
        var pageType = App.GetService<LauncherFrontendRegistry>().Get(destination.ProviderId).StorePageType;
        if (pageType is null) return;
        _hub.Visibility = Visibility.Collapsed;
        _providerFrame.Visibility = Visibility.Visible;
        _providerFrame.Navigate(pageType, destination.Uri);
    }
}
