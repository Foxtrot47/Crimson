using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class ManagerCharacterizationTests : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly List<string> _storageRoots = [];

    [Fact]
    public void LibraryManager_ReturnsOwnedGameAndItsDlcs()
    {
        var baseGame = Game("base", metadataId: "catalog-base");
        var matchingDlc = Game("matching-dlc", mainGameId: "catalog-base");
        var otherDlc = Game("other-dlc", mainGameId: "other-catalog");
        var storage = StorageWith(baseGame, matchingDlc, otherDlc);
        var manager = LibraryManagerWith(storage);

        Assert.Same(baseGame, manager.GetGameInfo("base"));
        Assert.Null(manager.GetGameInfo("missing"));
        Assert.Equal([matchingDlc], manager.GetDlcsForGame("base"));
        Assert.Empty(manager.GetDlcsForGame("matching-dlc"));
        Assert.Empty(manager.GetDlcsForGame("missing"));
    }

    [Fact]
    public void InstallManager_QueuesValidActionsAndPreservesOrder()
    {
        var installable = Game("installable");
        var installed = Game("installed", InstallState.Installed);
        var broken = Game("broken", InstallState.Broken);
        var manager = InstallManagerWith(installable, installed, broken);
        SetCurrentInstall(manager, new InstallItem("active", ActionType.Install, "C:\\active"));

        var install = new InstallItem("installable", ActionType.Install, "C:\\installable");
        var duplicate = new InstallItem("installable", ActionType.Install, "C:\\duplicate");
        var update = new InstallItem("installed", ActionType.Update, "C:\\installed");
        var forcedRepair = new InstallItem("broken", ActionType.Update, "C:\\broken");

        manager.AddToQueue(install);
        manager.AddToQueue(duplicate);
        manager.AddToQueue(new InstallItem("missing", ActionType.Install, "C:\\missing"));
        manager.AddToQueue(new InstallItem("installable", ActionType.Update, "C:\\installable"));
        manager.AddToQueue(update);
        manager.AddToQueue(forcedRepair);

        Assert.Equal(["installable", "installed", "broken"], manager.GetQueueItemNames());
        Assert.Same(install, manager.GameGameInQueue("installable"));
        Assert.Same(update, manager.GameGameInQueue("installed"));
        Assert.Equal(ActionType.Repair, forcedRepair.Action);

        manager.CancelInstall("installed");

        Assert.Equal(["installable", "broken"], manager.GetQueueItemNames());
        Assert.Null(manager.GameGameInQueue("installed"));
    }

    [Fact]
    public void InstallManager_HistoryReturnsLatestEntryPerGameInStableOrder()
    {
        var manager = InstallManagerWith();
        var history = GetPrivateField<List<InstallItem>>(manager, "_installHistory");
        history.Add(new InstallItem("alpha", ActionType.Install, "C:\\alpha"));
        history.Add(new InstallItem("beta", ActionType.Install, "C:\\beta"));
        history.Add(new InstallItem("alpha", ActionType.Repair, "C:\\alpha"));

        Assert.Equal(["beta", "alpha"], manager.GetHistoryItemsNames());
    }

    [Fact]
    public void LibraryManager_MissingInstallDirectoryMarksGameNotInstalled()
    {
        var game = Game("missing-install", InstallState.Installed);
        game.LocalAppState!.InstallPath = Path.Combine(
            Path.GetTempPath(),
            $"crimson-missing-{Guid.NewGuid():N}");
        var storage = StorageWith(game);
        var manager = LibraryManagerWith(storage);

        var reconciled = manager.GetGameInfo(game.AppName);

        Assert.NotNull(reconciled);
        Assert.Equal(InstallState.NotInstalled, reconciled.LocalAppState?.InstallStatus);
        Assert.Null(reconciled.LocalAppState?.InstallPath);
        var installManager = new InstallManager(
            _logger,
            manager,
            new UnusedStoreRepository(),
            storage,
            new DownloadManager(_logger, new HttpClient()));
        installManager.AddToQueue(new InstallItem(game.AppName, ActionType.Repair, game.LocalAppState.InstallPath));
        Assert.Empty(installManager.GetQueueItemNames());
    }

    private LibraryManager LibraryManagerWith(Storage storage)
    {
        var auth = new AuthManager(_logger, storage, new HttpClient());
        return new LibraryManager(_logger, new UnusedStoreRepository(), storage, auth);
    }

    private InstallManager InstallManagerWith(params Game[] games)
    {
        var storage = StorageWith(games);
        var library = LibraryManagerWith(storage);
        var downloads = new DownloadManager(_logger, new HttpClient());
        return new InstallManager(_logger, library, new UnusedStoreRepository(), storage, downloads);
    }

    private Storage StorageWith(params Game[] games)
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        var storage = new Storage(_logger, root);
        foreach (var game in games)
        {
            storage.SaveMetaData(game);
            if (game.LocalAppState != null)
                storage.AddToLocalAppState(game.AppName, game.LocalAppState);
        }
        return storage;
    }

    private static Game Game(
        string appName,
        InstallState installState = InstallState.NotInstalled,
        string? metadataId = null,
        string? mainGameId = null) => new()
    {
        AppName = appName,
        AppTitle = appName,
        AssetInfos = new AssetInfos
        {
            Windows = new Asset
            {
                AppName = appName,
                BuildVersion = "1.0.0",
                CatalogItemId = metadataId ?? appName,
                Namespace = "synthetic"
            }
        },
        Metadata = new Metadata
        {
            Id = metadataId ?? appName,
            MainGameItem = mainGameId == null ? null! : new MainGameItem { Id = mainGameId }
        },
        LocalAppState = new LocalAppState
        {
            AppName = appName,
            InstallStatus = installState,
            InstallPath = installState == InstallState.NotInstalled ? null : Path.GetTempPath()
        }
    };

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
        foreach (var root in _storageRoots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void SetCurrentInstall(InstallManager manager, InstallItem item) =>
        typeof(InstallManager)
            .GetProperty(nameof(InstallManager.CurrentInstall), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(manager, item);

    private static T GetPrivateField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SetPrivateField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class UnusedStoreRepository : IStoreRepository
    {
        public Task<RepositoryResult<Metadata>> FetchGameMetaData(
            string nameSpace,
            string catalogItemId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<byte[]>> GetGameManifest(
            GetManifestUrlData urlData,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<long>> DownloadFileAsync(
            string url,
            string destinationPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<string>> GetGameToken(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
