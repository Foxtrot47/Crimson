namespace Crimson.ViewModels;

public sealed class LibraryItem
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string OperationStatus { get; init; } = string.Empty;
    public bool HasActiveOperation { get; init; }
    public bool IsOperationIndeterminate { get; init; }
    public int OperationProgress { get; init; }
}
