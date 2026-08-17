using System.Reflection;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Infrastructure;
using Crimson.Utils;
using Serilog;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task LibraryManager_PersistsDetectedUpdateAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        var installRoot = Path.Combine(root, "installed-game");
        Directory.CreateDirectory(installRoot);
        var storage = new Storage(_logger, root);
        var game = Game("update-game", InstallState.Installed);
        game.LocalAppState!.InstallPath = installRoot;
        game.LocalAppState.Version = "1.0.0";
        storage.SaveMetaData(game);
        storage.AddToLocalAppState(game.AppName, game.LocalAppState);
        var available = new Asset
        {
            AppName = game.AppName,
            BuildVersion = "2.0.0",
            CatalogItemId = "update-catalog",
            Namespace = "update-namespace"
        };
        var repository = new RefreshStoreRepository(
            available,
            new Metadata
            {
                Id = available.CatalogItemId,
                Namespace = available.Namespace,
                Title = "Update Game"
            });
        var authentication = new AuthManager(_logger, storage, new HttpClient());
        var manager = new LibraryManager(_logger, repository, storage, authentication);
        Game? published = null;
        manager.LibraryUpdated += games => published = games.Single(candidate => candidate.AppName == game.AppName);

        var result = await manager.GetLibraryData(forceUpdate: true);

        Assert.Equal(InstallState.NeedUpdate, result.Single().LocalAppState?.InstallStatus);
        Assert.Equal(InstallState.NeedUpdate, published?.LocalAppState?.InstallStatus);
        var restarted = new Storage(_logger, root);
        Assert.Equal(
            InstallState.NeedUpdate,
            restarted.LocalAppStateDictionary[game.AppName].InstallStatus);
    }


    [Fact]
    public async Task LibraryManager_LaunchesInstalledExecutableAfterFetchingGameToken()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        var installRoot = Path.Combine(root, "installed-game");
        Directory.CreateDirectory(installRoot);
        File.Copy(Environment.GetEnvironmentVariable("COMSPEC")!, Path.Combine(installRoot, "LaunchStub.exe"));
        var storage = new Storage(_logger, root);
        var game = Game("launch-game", InstallState.Installed);
        game.LocalAppState!.InstallPath = installRoot;
        game.LocalAppState.Executable = "LaunchStub.exe";
        storage.SaveMetaData(game);
        storage.AddToLocalAppState(game.AppName, game.LocalAppState);
        await storage.SaveUserData(new UserData
        {
            AccountId = "test-account",
            DisplayName = "Test Player"
        });
        var authentication = new AuthManager(_logger, storage, new HttpClient());
        SetPrivateField(authentication, "_authenticationStatus", AuthenticationStatus.LoggedIn);
        var repository = new LaunchStoreRepository();
        var manager = new LibraryManager(_logger, repository, storage, authentication);

        await manager.LaunchApp(game.AppName);

        Assert.Equal(1, repository.GameTokenRequests);
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
            new DownloadManager(NullLogger<DownloadManager>.Instance, new HttpClient()));
        installManager.AddToQueue(new InstallItem(game.AppName, ActionType.Repair, game.LocalAppState.InstallPath));
        Assert.Empty(installManager.GetQueueItemNames());
    }

    [Fact]
    public void Storage_LoadsOnlyCanonicalMetadataFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        var metadataRoot = Path.Combine(root, "metadata");
        Directory.CreateDirectory(metadataRoot);
        var canonical = Game("canonical");
        AtomicJsonFile.Write(
            Path.Combine(metadataRoot, $"{StorageKeyCodec.Encode(canonical.AppName)}.json"),
            canonical,
            JsonStateSchemas.GameMetadata);
        File.WriteAllText(
            Path.Combine(metadataRoot, "legacy.json"),
            JsonSerializer.Serialize(Game("legacy")));

        var storage = new Storage(_logger, root);

        Assert.Equal([canonical.AppName], storage.GameMetaDataDictionary.Keys);
    }

    [Fact]
    public void Storage_MigratesHistoricalSettingsAndInstallState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var installStatePath = Path.Combine(root, "install_state.json");
        var rawSettings = JsonSerializer.Serialize(new Settings
        {
            MicaEnabled = true,
            DefaultInstallLocation = @"E:\Games"
        });
        const string rawInstallState =
            "{\"CurrentInstall\":null,\"IoQueue\":null,\"CompletedChunks\":null}";
        File.WriteAllText(settingsPath, rawSettings);
        File.WriteAllText(installStatePath, rawInstallState);
        var storage = new Storage(_logger, root);

        var settings = storage.GetSettings();
        var installState = storage.GetInstallState();

        Assert.True(settings?.MicaEnabled);
        Assert.Equal(@"E:\Games", settings?.DefaultInstallLocation);
        Assert.Equal(rawInstallState, installState);
        using var migratedSettings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        using var migratedInstallState = JsonDocument.Parse(File.ReadAllText(installStatePath));
        Assert.Equal(2, migratedSettings.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(1, migratedInstallState.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(rawSettings, File.ReadAllText(settingsPath + ".bak"));
        Assert.Equal(rawInstallState, File.ReadAllText(installStatePath + ".bak"));
    }

    [Fact]
    public void Storage_RejectsFutureAuthoritativeInstallationSchemaWithoutModification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "localstate.json");
        const string future = "{\"Version\":2,\"Data\":{}}";
        File.WriteAllText(path, future);

        Assert.Throws<InvalidDataException>(() => new Storage(_logger, root));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public async Task Storage_IndexesContentAddressedManifestCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-storage-{Guid.NewGuid():N}");
        _storageRoots.Add(root);
        var storage = new Storage(_logger, root);
        byte[] manifest = [1, 2, 3, 4];

        await storage.CacheManifestBytes("game-one", "v1", manifest);
        await storage.CacheManifestBytes("game-two", "v2", manifest);

        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "manifests"), "*.manifest"));
        var restarted = new Storage(_logger, root);
        Assert.Equal(manifest, await restarted.GetCachedManifestBytes("game-one", "v1"));
        Assert.Equal(manifest, await restarted.GetCachedManifestBytes("game-two", "v2"));
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
        var downloads = new DownloadManager(NullLogger<DownloadManager>.Instance, new HttpClient());
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

    private sealed class RefreshStoreRepository(Asset asset, Metadata metadata) : IStoreRepository
    {
        public Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(EpicPayloadPlatform.Windows, platform);
            return Task.FromResult(RepositoryResult<IReadOnlyList<Asset>>.Success([asset]));
        }

        public Task<RepositoryResult<Metadata>> FetchGameMetaData(
            string nameSpace,
            string catalogItemId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(asset.Namespace, nameSpace);
            Assert.Equal(asset.CatalogItemId, catalogItemId);
            return Task.FromResult(RepositoryResult<Metadata>.Success(metadata));
        }

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

    private sealed class LaunchStoreRepository : IStoreRepository
    {
        public int GameTokenRequests { get; private set; }

        public Task<RepositoryResult<string>> GetGameToken(
            CancellationToken cancellationToken = default)
        {
            GameTokenRequests++;
            return Task.FromResult(RepositoryResult<string>.Success("{\"code\":\"launch-code\"}"));
        }

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

        public Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

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
