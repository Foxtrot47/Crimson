using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.ViewModels;

public enum CacheCategoryKind
{
    Artwork,
    Manifests,
    Metadata,
    StoreDetails,
    Library
}

public sealed partial class CacheCategory : ObservableObject
{
    public CacheCategoryKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool CanClear { get; init; }

    [ObservableProperty]
    private long _bytes;

    [ObservableProperty]
    private string _sizeText = string.Empty;
}
