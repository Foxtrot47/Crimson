using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.ViewModels;

public sealed partial class LibraryItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }

    [ObservableProperty]
    private string _operationStatus = string.Empty;

    [ObservableProperty]
    private bool _hasActiveOperation;

    [ObservableProperty]
    private bool _isOperationIndeterminate;

    [ObservableProperty]
    private int _operationProgress;
}
