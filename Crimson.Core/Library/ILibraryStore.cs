using Crimson.Models;

namespace Crimson.Core;

public sealed record LibraryStoreState(
    IReadOnlyList<Asset> Assets,
    IReadOnlyDictionary<string, Game> Games,
    IReadOnlyDictionary<string, LocalAppState> LocalInstallations);

public sealed record LibraryInstallationRefresh(
    string AppName,
    string AssetBuildVersion,
    string? AvailableManifestDigest);

public interface ILibraryStore
{
    LibraryStoreState Read();

    LibraryStoreState WriteRefresh(
        IReadOnlyList<Asset> assets,
        IReadOnlyList<Game> games,
        IReadOnlyList<LibraryInstallationRefresh> installationRefreshes);
}
