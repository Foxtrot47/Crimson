namespace Crimson.Core;

public sealed record GameSummary(
    string AppName,
    string Title,
    Uri? ImageUri,
    string BuildVersion,
    bool IsInstalled);

public sealed record LibrarySnapshot(
    long Sequence,
    IReadOnlyList<GameSummary> Games,
    string? Error = null);

public interface ILibraryService
{
    event EventHandler<LibrarySnapshot>? Changed;

    Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
