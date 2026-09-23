using Crimson.Models;

namespace Crimson.ViewModels;

public sealed class DownloadManagerItem
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public InstallState InstallState { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string FailureText { get; init; } = string.Empty;
    public bool HasFailure { get; init; }
    public bool CanRetry { get; init; }
    public OperationSnapshot? Operation { get; init; }
}
