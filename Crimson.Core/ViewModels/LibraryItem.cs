using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.ViewModels;

public sealed partial class LibraryItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? SourceImageUrl { get; init; }

    [ObservableProperty]
    private string? _imageUrl;
}
