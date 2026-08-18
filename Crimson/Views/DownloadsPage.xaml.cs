using Crimson.Presentation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>
/// Page where we list current and past downloads
/// </summary>
public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel => (DownloadsViewModel)DataContext;

    public DownloadsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<DownloadsViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.ActivateAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }
}

