using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Providers;

namespace Crimson.ViewModels;

public partial class InstallLanguageItem : ObservableObject
{
    public required string Code { get; init; }
    public required string DisplayName { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class InstallLanguageSelection : ObservableObject
{
    private HashSet<string> _initial = new(StringComparer.OrdinalIgnoreCase);
    private bool _adjusting;

    public ObservableCollection<InstallLanguageItem> Items { get; } = [];
    public bool AllowsMultiple { get; private set; }

    public bool HasOptions => Items.Count > 0;
    public bool HasSelection => Items.Any(item => item.IsSelected);
    public bool IsSelectionMissing => HasOptions && !HasSelection;
    public bool HasChanges => !_initial.SetEquals(SelectedCodes);
    public bool CanApply => HasSelection && HasChanges;

    public IReadOnlyList<string> SelectedCodes => Items.Where(item => item.IsSelected).Select(item => item.Code).ToList();

    /// <summary>Raised after the user changes the selection; not raised by <see cref="Load"/>.</summary>
    public event Action? SelectionChanged;

    /// <summary>Keeps only offered codes, once each, and a single code when the game allows only one.</summary>
    public static IReadOnlyList<string> Normalize(InstallLanguageChoice choice)
    {
        var offered = choice.Available.Select(option => option.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = choice.Selected.Where(offered.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return choice.AllowsMultiple ? selected : selected.Take(1).ToList();
    }

    public void Load(InstallLanguageChoice choice)
    {
        Detach();
        Items.Clear();
        AllowsMultiple = choice.AllowsMultiple;
        var selected = Normalize(choice).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in choice.Available.DistinctBy(option => option.Code, StringComparer.OrdinalIgnoreCase))
            Items.Add(new InstallLanguageItem
            {
                Code = option.Code, DisplayName = option.DisplayName, IsSelected = selected.Contains(option.Code)
            });
        _initial = selected;
        foreach (var item in Items) item.PropertyChanged += ItemChanged;
        RaiseFlags();
    }

    public void Clear()
    {
        Detach();
        Items.Clear();
        AllowsMultiple = false;
        _initial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RaiseFlags();
    }

    private void Detach()
    {
        foreach (var item in Items) item.PropertyChanged -= ItemChanged;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstallLanguageItem.IsSelected) || _adjusting) return;
        if (!AllowsMultiple && sender is InstallLanguageItem { IsSelected: true } chosen)
        {
            _adjusting = true;
            try
            {
                foreach (var item in Items)
                    if (!ReferenceEquals(item, chosen)) item.IsSelected = false;
            }
            finally
            {
                _adjusting = false;
            }
        }
        RaiseFlags();
        SelectionChanged?.Invoke();
    }

    private void RaiseFlags()
    {
        OnPropertyChanged(nameof(AllowsMultiple));
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionMissing));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanApply));
    }
}
