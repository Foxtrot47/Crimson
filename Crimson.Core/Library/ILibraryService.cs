using System.Collections.Immutable;
using Crimson.Models;
using Crimson.Repository;

namespace Crimson.Core;

public enum GameUpdateClassification
{
    NotInstalled,
    Current,
    UpdateAvailable,
    Unknown
}

public static class GameUpdateClassifier
{
    public static GameUpdateClassification Classify(
        LocalAppState local,
        string assetBuildVersion)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (local.InstallStatus == InstallState.NotInstalled)
            return GameUpdateClassification.NotInstalled;
        var availableDigest = NormalizeDigest(local.AvailableManifestDigest);
        var installedDigest = availableDigest?.Length switch
        {
            40 => NormalizeDigest(local.InstalledManifestSha1),
            64 => NormalizeDigest(local.InstalledManifestSha256),
            _ => null
        };
        if (installedDigest is not null && availableDigest is not null)
        {
            return string.Equals(installedDigest, availableDigest, StringComparison.Ordinal)
                ? GameUpdateClassification.Current
                : GameUpdateClassification.UpdateAvailable;
        }

        var installedBuild = local.InstalledManifestBuildVersion ?? local.Version;
        return string.IsNullOrWhiteSpace(installedBuild)
            ? GameUpdateClassification.Unknown
            : string.Equals(installedBuild, assetBuildVersion, StringComparison.Ordinal)
                ? GameUpdateClassification.Current
                : GameUpdateClassification.UpdateAvailable;
    }

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        var separator = normalized.IndexOf(':');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];
        return normalized.ToLowerInvariant();
    }
}

public sealed record GameSnapshot(
    string AppName,
    string Title,
    Uri? ImageUri,
    string Namespace,
    string CatalogItemId,
    string AssetBuildVersion,
    string? AvailableManifestDigest,
    string? InstalledManifestBuildVersion,
    string? InstalledManifestSha1,
    string? InstalledManifestSha256,
    InstallState InstallState,
    GameUpdateClassification UpdateClassification,
    string? InstallPath,
    string? Executable);

public sealed record LibrarySnapshot(
    long Sequence,
    DateTimeOffset RefreshedAt,
    ImmutableArray<GameSnapshot> Games)
{
    public static LibrarySnapshot Empty { get; } = new(0, DateTimeOffset.MinValue, []);
}

public enum LibraryRefreshFailureKind
{
    Repository,
    Storage,
    InvalidData
}

public sealed record LibraryRefreshFailure(
    LibraryRefreshFailureKind Kind,
    string Message,
    RepositoryFailureKind? RepositoryKind = null);

public sealed record LibraryRefreshResult(
    LibrarySnapshot Snapshot,
    LibraryRefreshFailure? Failure = null)
{
    public bool IsSuccess => Failure is null;
}

public interface ILibraryService
{
    LibrarySnapshot Snapshot { get; }

    event EventHandler<LibrarySnapshot>? Changed;

    Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<LibraryRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}
