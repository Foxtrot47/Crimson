using Crimson.ViewModels;
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Dispose();
        base.OnNavigatedFrom(e);
    }
}
