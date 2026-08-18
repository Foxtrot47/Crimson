using System.Collections.Immutable;
using Crimson.Models;
using Crimson.Repository;

namespace Crimson.Core;

public sealed class LibraryService : ILibraryService, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private readonly IStoreRepository _repository;
    private readonly ILibraryStore _store;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private LibrarySnapshot _snapshot;
    private bool _disposed;

    public LibraryService(IStoreRepository repository, ILibraryStore store)
    {
        _repository = repository;
        _store = store;
        _snapshot = BuildSnapshot(store.Read(), 0, DateTimeOffset.MinValue);
    }

    public LibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<LibrarySnapshot>? Changed;

    public Task<LibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public async Task<LibraryRefreshResult> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var observedSequence = Snapshot.Sequence;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (current.Sequence != observedSequence ||
                (!force && DateTimeOffset.UtcNow - current.RefreshedAt < RefreshInterval))
                return new LibraryRefreshResult(current);
            return await RefreshCoreAsync(current, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<LibraryRefreshResult> RefreshCoreAsync(
        LibrarySnapshot previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var assetResult = await _repository.FetchGameAssets(
                EpicPayloadPlatform.Windows,
                cancellationToken: cancellationToken);
            if (!assetResult.IsSuccess)
                return RepositoryFailure(previous, assetResult.Failure!);
            if (assetResult.Value.Count == 0)
            {
                return new LibraryRefreshResult(
                    previous,
                    new LibraryRefreshFailure(
                        LibraryRefreshFailureKind.InvalidData,
                        "Asset response did not contain any games."));
            }

            var assets = assetResult.Value
                .Where(IsSupportedAsset)
                .ToArray();
            var metadataResults = await Task.WhenAll(assets.Select(async asset =>
                (Asset: asset, Result: await _repository.FetchGameMetaData(
                    asset.Namespace,
                    asset.CatalogItemId,
                    cancellationToken))));
            var metadataFailure = metadataResults.FirstOrDefault(item => !item.Result.IsSuccess);
            if (metadataFailure.Result is { IsSuccess: false })
                return RepositoryFailure(previous, metadataFailure.Result.Failure!);

            var games = metadataResults.Select(item => new Game
            {
                AppName = item.Asset.AppName,
                AppTitle = item.Result.Value.Title,
                AssetInfos = new AssetInfos { Windows = item.Asset },
                Metadata = item.Result.Value
            }).ToArray();
            var state = _store.Read();
            var installedAssets = assets
                .Where(asset => state.LocalInstallations.TryGetValue(asset.AppName, out var local) &&
                    local.InstallStatus is InstallState.Installed or InstallState.NeedUpdate)
                .ToArray();
            var manifestResults = await Task.WhenAll(installedAssets.Select(async asset =>
                (Asset: asset, Result: await _repository.GetManifestUrls(
                    asset.Namespace,
                    asset.CatalogItemId,
                    asset.AppName,
                    EpicPayloadPlatform.Windows,
                    cancellationToken: cancellationToken))));
            var manifestFailure = manifestResults.FirstOrDefault(item => !item.Result.IsSuccess);
            if (manifestFailure.Result is { IsSuccess: false })
                return RepositoryFailure(previous, manifestFailure.Result.Failure!);

            var installationRefreshes = manifestResults
                .Select(item => new LibraryInstallationRefresh(
                    item.Asset.AppName,
                    item.Asset.BuildVersion,
                    NormalizeDigest(item.Result.Value.ManifestHash)))
                .ToArray();
            var persistedState = _store.WriteRefresh(assets, games, installationRefreshes);
            var refreshedAt = DateTimeOffset.UtcNow;
            var snapshot = BuildSnapshot(
                persistedState,
                checked(previous.Sequence + 1),
                refreshedAt);
            Volatile.Write(ref _snapshot, snapshot);
            Changed?.Invoke(this, snapshot);
            return new LibraryRefreshResult(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            return new LibraryRefreshResult(
                previous,
                new LibraryRefreshFailure(LibraryRefreshFailureKind.InvalidData, exception.Message));
        }
        catch (Exception exception)
        {
            return new LibraryRefreshResult(
                previous,
                new LibraryRefreshFailure(
                    LibraryRefreshFailureKind.Storage,
                    $"Library refresh failed with {exception.GetType().Name}."));
        }
    }

    private static LibraryRefreshResult RepositoryFailure(
        LibrarySnapshot previous,
        RepositoryFailure failure) => new(
        previous,
        new LibraryRefreshFailure(
            LibraryRefreshFailureKind.Repository,
            failure.Message,
            failure.Kind));

    private static bool IsSupportedAsset(Asset asset)
    {
        var ueIndex = asset.BuildVersion.IndexOf("UE", StringComparison.Ordinal);
        var windowsIndex = asset.BuildVersion.IndexOf("Windows", StringComparison.Ordinal);
        return !asset.Namespace.Contains("ue", StringComparison.Ordinal) &&
            (ueIndex < 0 || windowsIndex < ueIndex);
    }

    private static LibrarySnapshot BuildSnapshot(
        LibraryStoreState state,
        long sequence,
        DateTimeOffset refreshedAt)
    {
        var games = state.Games.Values
            .Where(game => !game.IsDlc())
            .Select(game => ToSnapshot(
                game,
                state.LocalInstallations.GetValueOrDefault(game.AppName)))
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        return new LibrarySnapshot(sequence, refreshedAt, games);
    }

    private static GameSnapshot ToSnapshot(Game game, LocalAppState? local)
    {
        var imageValue = game.Metadata.KeyImages?
            .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url;
        Uri? imageUri = null;
        if (Uri.TryCreate(imageValue, UriKind.Absolute, out var parsedImage) &&
            parsedImage.Scheme == Uri.UriSchemeHttps)
            imageUri = parsedImage;
        var classification = local is null
            ? GameUpdateClassification.NotInstalled
            : GameUpdateClassifier.Classify(local, game.AssetInfos.Windows.BuildVersion);
        return new GameSnapshot(
            game.AppName,
            game.AppTitle,
            imageUri,
            game.AssetInfos.Windows.Namespace,
            game.AssetInfos.Windows.CatalogItemId,
            game.AssetInfos.Windows.BuildVersion,
            local?.AvailableManifestDigest,
            local?.InstalledManifestBuildVersion ?? local?.Version,
            local?.InstalledManifestSha1,
            local?.InstalledManifestSha256,
            local?.InstallStatus ?? InstallState.NotInstalled,
            classification,
            local?.InstallPath,
            local?.Executable);
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _refreshGate.Dispose();
    }
}
