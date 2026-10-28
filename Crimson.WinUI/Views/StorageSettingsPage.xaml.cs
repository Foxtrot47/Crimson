using System;
using System.Linq;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>Settings sub-page showing cache usage per category.</summary>
public sealed partial class StorageSettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public StorageSettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.CacheUsageChanged += CacheUsageChanged;
        ActualThemeChanged += (_, _) => RenderCacheUsageBar();
        Loaded += (_, _) => RenderCacheUsageBar();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCacheUsageCommand.ExecuteAsync(null);
    }

    private void ParentClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void CacheUsageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RenderCacheUsageBar);

    private void RenderCacheUsageBar()
    {
        // Sizes can arrive before x:Bind and theme resources reach the cards; Loaded renders again.
        if (!IsLoaded) return;
        CacheUsageBar.ColumnDefinitions.Clear();
        CacheUsageBar.Children.Clear();
        var segments = new[]
            {
                (ViewModel.ArtworkCache, ArtworkCacheCard.Swatch),
                (ViewModel.ManifestCache, ManifestCacheCard.Swatch),
                (ViewModel.MetadataCache, MetadataCacheCard.Swatch),
                (ViewModel.StoreDetailsCache, StoreDetailsCacheCard.Swatch),
                (ViewModel.LibraryCache, LibraryCacheCard.Swatch)
            }
            .Where(segment => segment.Item1.Bytes > 0).ToList();
        for (var index = 0; index < segments.Count; index++)
        {
            var (category, swatch) = segments[index];
            CacheUsageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(category.Bytes, GridUnitType.Star) });
            // A 2px gap keeps adjacent segments distinguishable without relying on colour alone.
            var segment = new Border
            {
                Background = swatch,
                Margin = new Thickness(0, 0, index < segments.Count - 1 ? 2 : 0, 0)
            };
            var label = $"{category.Name}: {category.SizeText}";
            ToolTipService.SetToolTip(segment, label);
            AutomationProperties.SetName(segment, label);
            Grid.SetColumn(segment, index);
            CacheUsageBar.Children.Add(segment);
        }
    }
}
