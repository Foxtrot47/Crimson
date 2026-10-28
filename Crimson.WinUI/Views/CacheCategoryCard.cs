using System.Windows.Input;
using CommunityToolkit.WinUI.Controls;
using Crimson.Resources;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Crimson.Views;

/// <summary>One cache usage legend row: colour swatch, name, size and an optional Clear button.</summary>
public sealed partial class CacheCategoryCard : SettingsCard
{
    public static readonly DependencyProperty CategoryProperty = DependencyProperty.Register(
        nameof(Category), typeof(CacheCategory), typeof(CacheCategoryCard), new PropertyMetadata(null, OnCategoryChanged));

    public static readonly DependencyProperty SwatchProperty = DependencyProperty.Register(
        nameof(Swatch), typeof(Brush), typeof(CacheCategoryCard), new PropertyMetadata(null, OnSwatchChanged));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(CacheCategoryCard), new PropertyMetadata(null, OnClearCommandChanged));

    private readonly Rectangle _swatch = new() { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _size = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clear = new() { MinWidth = 80, Content = UiStrings.ClearCache, Visibility = Visibility.Collapsed };

    public CacheCategoryCard()
    {
        Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _swatch, _name } };
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _size, _clear } };
    }

    public CacheCategory? Category
    {
        get => (CacheCategory?)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    public Brush? Swatch
    {
        get => (Brush?)GetValue(SwatchProperty);
        set => SetValue(SwatchProperty, value);
    }

    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    private static void OnCategoryChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (CacheCategoryCard)sender;
        if (args.OldValue is CacheCategory old) old.PropertyChanged -= card.CategoryPropertyChanged;
        if (args.NewValue is CacheCategory category)
        {
            category.PropertyChanged += card.CategoryPropertyChanged;
            card._name.Text = category.Name;
            card._clear.CommandParameter = category;
        }
        card.UpdateSize();
    }

    private static void OnSwatchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((CacheCategoryCard)sender)._swatch.Fill = (Brush?)args.NewValue;

    private static void OnClearCommandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (CacheCategoryCard)sender;
        card._clear.Command = (ICommand?)args.NewValue;
        card._clear.Visibility = args.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CacheCategory.SizeText)) UpdateSize();
    }

    private void UpdateSize() => _size.Text = Category?.SizeText ?? string.Empty;
}
