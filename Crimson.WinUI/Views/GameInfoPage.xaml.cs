using Crimson.Resources;
using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Crimson.Views;

public sealed partial class GameInfoPage : Page
{
    private bool _isScreenshotDialogOpen;
    private CancellationTokenSource? _screenshotCancellation;

    public GameInfoViewModel ViewModel { get; }

    public GameInfoPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<GameInfoViewModel>();
        DataContext = ViewModel;

        // Subscribe to dialog and picker events
        ViewModel.ShowInstallDialogRequested += ShowInstallDialog;
        ViewModel.FolderPickerRequested += ShowFolderPicker;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.OnNavigatedTo(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CloseScreenshot();
        ManageDialog.Close();
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async Task ShowInstallDialog()
    {
        // Initialize the dialog
        InstallDialog.XamlRoot = this.XamlRoot;
        await InstallDialog.ShowAsync(ViewModel.Game);
    }

    private void HeroImageOpened(object sender, RoutedEventArgs e)
    {
        HeroImageLoadingRing.Visibility = Visibility.Collapsed;
        HeroImage.Opacity = 1;
    }

    private void HeroImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HeroImageLoadingRing.Visibility = Visibility.Collapsed;
    }

    private void CoverImageOpened(object sender, RoutedEventArgs e)
    {
        CoverImageLoadingRing.Visibility = Visibility.Collapsed;
        CoverImage.Opacity = 1;
    }

    private void CoverImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        CoverImageLoadingRing.Visibility = Visibility.Collapsed;
    }

    private void ScreenshotImageOpened(object sender, RoutedEventArgs e) =>
        CompleteScreenshotImage(sender as Image);

    private void ScreenshotImageFailed(object sender, ExceptionRoutedEventArgs e) =>
        HideScreenshotLoadingRing(sender as Image);

    private void PreviousScreenshotsClicked(object sender, RoutedEventArgs e) =>
        ScrollScreenshots(-1);

    private void NextScreenshotsClicked(object sender, RoutedEventArgs e) =>
        ScrollScreenshots(1);

    private void ScrollScreenshots(int direction)
    {
        var distance = Math.Max(320, ScreenshotScroller.ViewportWidth * 0.8);
        var destination = Math.Max(
            0,
            ScreenshotScroller.HorizontalOffset + (direction * distance));
        ScreenshotScroller.ChangeView(destination, null, null);
    }

    private async void ScreenshotClicked(object sender, RoutedEventArgs e)
    {
        if (_isScreenshotDialogOpen ||
            sender is not Button { Tag: StoreProductImage screenshot } ||
            !EpicEndpointPolicy.TryGetContentUri(screenshot.Url, out var uri))
            return;

        ExpandedScreenshotImage.Opacity = 0;
        ExpandedScreenshotErrorText.Visibility = Visibility.Collapsed;
        ExpandedScreenshotLoadingRing.Visibility = Visibility.Visible;
        ExpandedScreenshotLoadingRing.IsActive = true;
        AutomationProperties.SetName(
            ExpandedScreenshotImage,
            string.IsNullOrWhiteSpace(screenshot.AltText) ? UiStrings.GameScreenshot : screenshot.AltText);
        ExpandedScreenshotImage.Source = new BitmapImage(uri);
        SizeScreenshotViewer();
        ScreenshotDialog.XamlRoot = XamlRoot;
        _isScreenshotDialogOpen = true;
        using var cancellation = new CancellationTokenSource();
        _screenshotCancellation = cancellation;
        try
        {
            await App.GetService<DialogCoordinator>().ShowAsync(
                async () => await ScreenshotDialog.ShowAsync(), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isScreenshotDialogOpen = false;
            if (ReferenceEquals(_screenshotCancellation, cancellation)) _screenshotCancellation = null;
            ExpandedScreenshotLoadingRing.IsActive = false;
            ExpandedScreenshotImage.Source = null;
        }
    }

    private void ScreenshotDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        ScreenshotCloseButton.Focus(FocusState.Programmatic);

    private void CloseScreenshotClicked(object sender, RoutedEventArgs e) =>
        CloseScreenshot();

    private void ScreenshotDialogKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        CloseScreenshot();
        e.Handled = true;
    }

    private void CloseScreenshot()
    {
        _screenshotCancellation?.Cancel();
        if (_isScreenshotDialogOpen)
            ScreenshotDialog.Hide();
    }

    private void GameInfoPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isScreenshotDialogOpen)
            SizeScreenshotViewer();
        if (ManageDialog.IsOpen)
            ManageDialog.UpdateSize(ActualWidth, ActualHeight);
    }

    private async void ManageClicked(object sender, RoutedEventArgs e)
    {
        var storeUri = await ManageDialog.ShowAsync(ViewModel, XamlRoot, ActualWidth, ActualHeight);
        NavigateToStore(storeUri);
    }

    private void ViewInStoreClicked(object sender, RoutedEventArgs e) =>
        NavigateToStore(ViewModel.StorePageUri);

    private static void NavigateToStore(Uri? uri)
    {
        if (uri is not null && ((App)Application.Current).GetWindow() is MainWindow window)
            window.NavigateToStore(uri);
    }

    private void SizeScreenshotViewer()
    {
        const double aspectRatio = 16d / 9d;
        var availableWidth = Math.Max(240, Math.Min(ActualWidth - 160, 1080));
        var availableHeight = Math.Max(135, Math.Min(ActualHeight - 160, 700));
        var width = Math.Min(availableWidth, availableHeight * aspectRatio);

        ScreenshotViewer.Width = width;
        ScreenshotViewer.Height = width / aspectRatio;
    }

    private void ExpandedScreenshotImageOpened(object sender, RoutedEventArgs e)
    {
        ExpandedScreenshotLoadingRing.IsActive = false;
        ExpandedScreenshotLoadingRing.Visibility = Visibility.Collapsed;
        ExpandedScreenshotErrorText.Visibility = Visibility.Collapsed;
        ExpandedScreenshotImage.Opacity = 1;
    }

    private void ExpandedScreenshotImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ExpandedScreenshotLoadingRing.IsActive = false;
        ExpandedScreenshotLoadingRing.Visibility = Visibility.Collapsed;
        ExpandedScreenshotErrorText.Visibility = Visibility.Visible;
    }

    private static void CompleteScreenshotImage(Image? image)
    {
        if (image is null) return;
        HideScreenshotLoadingRing(image);
        image.Opacity = 1;
    }

    private static void HideScreenshotLoadingRing(Image? image)
    {
        if (image?.Parent is not Grid container) return;
        if (container.FindName("ScreenshotLoadingRing") is ProgressRing ring)
            ring.Visibility = Visibility.Collapsed;
    }

    private async Task<string> ShowFolderPicker()
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var window = ((App)Microsoft.UI.Xaml.Application.Current).GetWindow();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync().AsTask();
        return folder?.Path;
    }
}
