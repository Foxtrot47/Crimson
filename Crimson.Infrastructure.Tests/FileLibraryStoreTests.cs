using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Models;
using Xunit;

namespace Crimson.Infrastructure.Tests;

public sealed class FileLibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-library-store-{Guid.NewGuid():N}");

    [Fact]
    public void WriteRefreshPersistsOneCompleteLibraryState()
    {
        var asset = new Asset
        {
            AppName = "game",
            BuildVersion = "2.0",
            CatalogItemId = "catalog",
            Namespace = "namespace"
        };
        var game = new Game
        {
            AppName = asset.AppName,
            AppTitle = "Game",
            AssetInfos = new AssetInfos { Windows = asset },
            Metadata = new Metadata
            {
                Id = asset.CatalogItemId,
                Namespace = asset.Namespace,
                Title = "Game",
                KeyImages = []
            }
        };
        var local = new LocalAppState
        {
            AppName = asset.AppName,
            InstallStatus = InstallState.Installed,
            InstalledManifestBuildVersion = "2.0",
            InstalledManifestSha256 = "digest"
        };
        var store = new FileLibraryStore(_root);
        AtomicJsonFile.Write(
            Path.Combine(_root, "localstate.json"),
            new Dictionary<string, LocalAppState> { [asset.AppName] = local },
            JsonStateSchemas.LocalInstallations);

        store.WriteRefresh(
            [asset],
            [game],
            [new LibraryInstallationRefresh(asset.AppName, asset.BuildVersion, "digest")]);
        var restarted = new FileLibraryStore(_root).Read();

        Assert.Equal("2.0", Assert.Single(restarted.Assets).BuildVersion);
        Assert.Equal("Game", restarted.Games[asset.AppName].AppTitle);
        Assert.Equal("digest", restarted.LocalInstallations[asset.AppName].InstalledManifestSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
