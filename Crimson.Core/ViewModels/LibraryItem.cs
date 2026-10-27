using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.ViewModels;

public sealed partial class LibraryItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? SourceImageUrl { get; init; }

    // The artwork cache generation ImageUrl was resolved against; clearing the cache invalidates it.
    internal int ArtworkGeneration { get; init; }

    // Set while artwork is loading so overlapping refreshes do not request it twice.
    internal bool ArtworkLoading { get; set; }

    // Set once a load has been attempted; only library updates retry a failed one.
    internal bool ArtworkAttempted { get; set; }

    [ObservableProperty]
    private string? _imageUrl;

    [ObservableProperty]
    private string _operationStatus = string.Empty;

    [ObservableProperty]
    private bool _hasActiveOperation;

    [ObservableProperty]
    private bool _isOperationIndeterminate;

    [ObservableProperty]
    private int _operationProgress;
}
