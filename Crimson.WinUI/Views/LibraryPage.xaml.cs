using Crimson.Controls;
using Crimson.Providers;
using Crimson.Resources;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
namespace Crimson.Views;

/// <summary>
/// Library Page which shows list of currently installed games
/// </summary>
public sealed partial class LibraryPage : Page
{
    private const float CardHoverScale = 1.04f;
    private static readonly TimeSpan CardHoverDuration = TimeSpan.FromMilliseconds(150);

    public LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    public IReadOnlyList<StoreAccountItem> StoreAccounts { get; }

    /// <summary>Stores whose installed games can be imported; the Import button hides when none can.</summary>
    public IReadOnlyList<StoreAccountItem> ImportSources { get; }
    public bool HasImportSources => ImportSources.Count > 0;

    public LibraryPage()
    {
        StoreAccounts = StoreAccountItem.Create(
            App.GetService<LauncherAuthentication>().Accounts, App.GetService<LauncherFrontendRegistry>());
        ImportSources = StoreAccounts.Where(source => source.Import is not null).ToList();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        DataContext = App.GetService<LibraryViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.ShowInstallDialogRequested -= ShowInstallDialog;
        ViewModel.ShowInstallDialogRequested += ShowInstallDialog;
        await ViewModel.OnNavigatedTo(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ShowInstallDialogRequested -= ShowInstallDialog;
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.ActionErrorMessage = string.Empty;
            await new ImportSourceDialog(ImportSources) { XamlRoot = XamlRoot }.RunAsync();
        }
        catch (Exception error)
        {
            App.GetService<Serilog.ILogger>().Error("Import dialog failed: {ErrorType}", error.GetType().Name);
            ViewModel.ActionErrorMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat,
                UiStrings.TheImportWindowCouldNotBeOpened, UiStrings.TryAgainOrRestartCrimson);
        }
    }

    private void GameButton_Click(object sender, RoutedEventArgs e)
    {
        var clickedButton = (Button)sender;
        var game = (LibraryItem)clickedButton.DataContext;
        var navControl = FindParentFrame(this);

        if (navControl == null)
            return;

        navControl.Navigate(typeof(GameInfoPage), game.Name);
    }

    private void ManageAccounts_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));

    private void GameCard_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetCardHover((FrameworkElement)sender, hovered: true);

    private void GameCard_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetCardHover((FrameworkElement)sender, hovered: false);

    private static void SetCardHover(FrameworkElement card, bool hovered)
    {
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.CenterPoint = new Vector3((float)card.ActualWidth / 2f, (float)card.ActualHeight / 2f, 0f);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        var factor = hovered ? CardHoverScale : 1f;
        scale.InsertKeyFrame(1f, new Vector3(factor, factor, 1f));
        scale.Duration = CardHoverDuration;
        visual.StartAnimation("Scale", scale);

        if (card.FindName("CardOverlay") is not FrameworkElement overlay) return;
        // Visibility, not opacity: an overlay laid out at zero opacity never rasterizes its
        // text, so the title stayed blank until some later repaint.
        overlay.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowInstallDialog(LauncherGame game)
    {
        InstallDialog.XamlRoot = XamlRoot;
        await InstallDialog.ShowAsync(game);
    }

    private static Frame FindParentFrame(DependencyObject child)
    {
        var parent = VisualTreeHelper.GetParent(child);

        while (parent != null && parent is not Microsoft.UI.Xaml.Controls.Frame)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        return parent as Frame;
    }
}
