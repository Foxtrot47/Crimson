using Crimson.Resources;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;
using Serilog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
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
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) =>
        {
            UpdateActiveLayout();
            DrawTransferGraph();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadsViewModel.TransferHistory)) DrawTransferGraph();
    }

    private void DownloadsPage_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateActiveLayout();

    private void UpdateActiveLayout()
    {
        if (DataContext is not DownloadsViewModel) return;
        var state = ActualWidth >= 840 ? "Wide" : "Compact";
        VisualStateManager.GoToState(this, state, false);
    }

    private void RateCanvas_SizeChanged(object sender, SizeChangedEventArgs args) => DrawTransferGraph();

    private void DrawTransferGraph()
    {
        if (DataContext is not DownloadsViewModel viewModel || RateCanvas.ActualWidth <= 0 || RateCanvas.ActualHeight <= 0) return;
        var history = viewModel.TransferHistory;
        var peak = history.Count == 0 ? 1 : Math.Max(1d / 1024, history.Max(sample => Math.Max(sample.DownloadMiBPerSecond, sample.WriteMiBPerSecond)));
        var scale = Math.Pow(2, Math.Ceiling(Math.Log2(peak)));
        GraphWaitingLabel.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RateCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, RateCanvas.ActualWidth, RateCanvas.ActualHeight) };
        DrawDownloadBars(CreateGraphPoints(history, sample => sample.DownloadMiBPerSecond, scale));
        DiskWriteRateLine.Points = CreateGraphPoints(history, sample => sample.WriteMiBPerSecond, scale);
    }

    private void DrawDownloadBars(PointCollection points)
    {
        var bars = new GeometryGroup();
        foreach (var point in points)
            bars.Children.Add(new LineGeometry
            {
                StartPoint = new Point(point.X, RateCanvas.ActualHeight - 1),
                EndPoint = point
            });
        DownloadRateBars.Data = bars;
        DownloadRateBars.StrokeThickness = Math.Clamp(RateCanvas.ActualWidth / 60 * 0.55, 1, 6);
    }

    private PointCollection CreateGraphPoints(
        IReadOnlyList<TransferRateSample> history, Func<TransferRateSample, double> rate, double scale)
    {
        var points = new PointCollection();
        var now = history.LastOrDefault()?.Seconds ?? 0;
        foreach (var sample in history)
        {
            points.Add(new Point(
                Math.Clamp(1 - (now - sample.Seconds) / 60, 0, 1) * RateCanvas.ActualWidth,
                (1 - Math.Clamp(rate(sample) / scale, 0, 1)) * (RateCanvas.ActualHeight - 2) + 1));
        }
        return points;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
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

        var copied = OperationDiagnosticActions.TryCopy(operation, diagnostic =>
        {
            var package = new DataPackage();
            package.SetText(diagnostic);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }, App.GetService<ILogger>());
        if (sender is Button button)
        {
            button.Content = copied ? UiStrings.Copied
                : UiStrings.Format(UiStrings.TitleDetailFormat, UiStrings.CopyDiagnostics, UiStrings.TryAgain);
            ToolTipService.SetToolTip(button, copied ? UiStrings.Copied : UiStrings.TryAgain);
        }
    }

    private async void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || GetItem(sender)?.Operation is not { } operation) return;
        button.IsEnabled = false;
        try
        {
            var opened = await OperationDiagnosticActions.TryReportAsync(operation,
                async uri => await Launcher.LaunchUriAsync(uri), App.GetService<ILogger>());
            button.Content = opened ? UiStrings.ReportProblem
                : UiStrings.Format(UiStrings.TitleDetailFormat, UiStrings.ReportProblem, UiStrings.TryAgain);
            ToolTipService.SetToolTip(button, opened ? UiStrings.ReportProblem
                : UiStrings.TheReportPageCouldNotBeOpenedErrorCodeReportOpenFailed);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static DownloadManagerItem? GetItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as DownloadManagerItem;
}
