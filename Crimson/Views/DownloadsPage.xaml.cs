using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

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
}

public class DownloadManagerItem
{
    public string Name { get; set; }
    public string Title { get; set; }
    public BitmapImage Image { get; set; }
    public InstallState InstallState { get; set; }
}
