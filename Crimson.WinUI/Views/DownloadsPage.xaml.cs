using System;
using Crimson.Core;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

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

    private void RetryOperation_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
            ViewModel.RetryFailedOperation(item);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender)?.Operation is not { } operation) return;

        var package = new DataPackage();
        package.SetText(OperationDiagnostics.Create(operation));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        if (sender is Button button)
            button.Content = "Copied";
    }

    private async void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender)?.Operation is { } operation)
            await Launcher.LaunchUriAsync(OperationDiagnostics.CreateIssueUri(operation));
    }

    private static DownloadManagerItem? GetItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as DownloadManagerItem;
}
