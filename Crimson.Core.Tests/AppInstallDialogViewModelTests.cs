using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class AppInstallDialogViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-install-dialog-{Guid.NewGuid():N}");
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task OlderInitializationCannotOverwriteNewerGame()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var firstGame = CreateGame("first", "First", "first-box");
        var secondGame = CreateGame("second", "Second", "second-box");
        storage.SaveMetaData(firstGame);
        storage.SaveMetaData(secondGame);
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(
            _logger,
            library,
            repository,
            storage,
            new DownloadManager(_logger, new HttpClient()),
            new UnusedShortcutManager(),
            new AllowInstallPermissionChecker());
        var viewModel = new AppInstallDialogViewModel(
            _logger,
            installer,
            library,
            storage,
            new ImmediateUiDispatcher());

        var firstInitialization = viewModel.InitializeAsync(firstGame);
        var secondInitialization = viewModel.InitializeAsync(secondGame);
        repository.Complete("second", CreateJsonManifest(downloadBytes: 200, writeBytes: 20));
        await secondInitialization;
        repository.Complete("first", CreateJsonManifest(downloadBytes: 100, writeBytes: 10));
        await firstInitialization;

        Assert.Equal("Second", viewModel.GameTitle);
        Assert.Equal("second-box", viewModel.GameImageUrl);
        Assert.Equal("200 B", viewModel.TotalDownloadSize);
        Assert.Equal("20 B", viewModel.TotalInstallSize);
        Assert.False(viewModel.IsLoadingContent);
    }

    [Fact]
    public async Task ObsoleteInitializationCannotInvalidateCurrentDriveRequest()
    {
        var repository = new ControlledManifestRepository();
        var volume = new BlockingVolumeResolver();
        var dispatcher = new BlockingFirstDispatcher();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"), volume);
        var first = CreateGame("first", "First", "first-box");
        var second = CreateGame("second", "Second", "second-box");
        storage.SaveMetaData(first);
        storage.SaveMetaData(second);
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
        var vm = new AppInstallDialogViewModel(_logger, installer, library, storage, dispatcher);

        var oldInitialization = vm.InitializeAsync(first);
        repository.Complete("first", CreateJsonManifest(100, 10));
        await dispatcher.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentInitialization = vm.InitializeAsync(second);
        repository.Complete("second", CreateJsonManifest(200, 20));
        try
        {
            await volume.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatcher.Release.Set();
            await oldInitialization.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            dispatcher.Release.Set();
            volume.Release.Set();
        }
        await currentInitialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsLoadingContent);
        Assert.True(vm.IsDriveSpaceVisible);
        Assert.Equal("Second", vm.GameTitle);
        Assert.Equal("20 B", vm.TotalInstallSize);
    }

    [Fact]
    public async Task CancellationReleasesBlockedManifestInitialization()
    {
        var repository = new ControlledManifestRepository();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.ManifestRequested += _ => requested.TrySetResult();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var game = CreateGame("game", "Game", "box");
        storage.SaveMetaData(game);
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
        var viewModel = new AppInstallDialogViewModel(
            _logger, installer, library, storage, new ImmediateUiDispatcher());
        var requestedClose = false;
        viewModel.RequestClose += () => requestedClose = true;
        using var cancellation = new CancellationTokenSource();

        var initialization = viewModel.InitializeAsync(game, cancellation.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(requestedClose);
    }

    [Fact]
    public async Task InstallDialogInitializationFailureStaysInlineAndKeepsTheDialogOpen()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var game = CreateGame("game", "Game", "box");
        storage.SaveMetaData(game);
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
        var errors = new List<OperationSnapshot>();
        installer.OperationError += errors.Add;
        var viewModel = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher());
        var closed = false;
        viewModel.RequestClose += () => closed = true;
        repository.Fail("game", new HttpRequestException("synthetic-private-url"));

        await viewModel.InitializeAsync(game);

        Assert.False(closed);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.CanInstall);
        Assert.Contains("Crimson could not download", viewModel.ErrorMessage);
        var error = Assert.Single(errors);
        Assert.False(error.ShowErrorDialog);
        Assert.Equal(OperationPhase.Preparing, error.Phase);
        Assert.Equal("network_unavailable", error.Failure?.Code);
        Assert.DoesNotContain("synthetic-private-url", OperationDiagnostics.Create(error));
    }

    [Fact]
    public async Task FailedMoveCannotCompleteNextUninstallBeforeItsManifestReturns()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "uninstall");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "game.exe");
        File.WriteAllText(file, "synthetic game data");
        var move = CreateGame("move", "Move", "image");
        var uninstall = CreateGame("uninstall", "Uninstall", "image");
        foreach (var (game, path) in new[] { (move, source), (uninstall, target) })
        {
            game.LocalAppState = new LocalAppState {
                AppName = game.AppName, InstallPath = path, InstallStatus = InstallState.Installed
            };
            storage.SaveMetaData(game);
            storage.AddToLocalAppState(game.AppName, game.LocalAppState);
        }
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.ManifestRequested += app => { if (app == "uninstall") requested.TrySetResult(); };
        installer.InstallationStatusChanged += item => {
            if (item.AppName == "uninstall" && item.Status == ActionStatus.Success) success.TrySetResult();
        };
        installer.AddToQueue(new InstallItem("move", ActionType.Move, source) {
            MoveLocation = Path.Combine(_root, "missing-parent", "destination")
        });
        installer.AddToQueue(new InstallItem("uninstall", ActionType.Uninstall, target));
        repository.Complete("move", CreateJsonManifest(100, 10));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAny(success.Task, Task.Delay(3000));
        var completedEarly = success.Task.IsCompleted;
        repository.Complete("uninstall", CreateJsonManifest(100, 10));
        await success.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(completedEarly);
        Assert.False(File.Exists(file));
        Assert.Equal(InstallState.NotInstalled, uninstall.LocalAppState!.InstallStatus);
    }

    [Fact]
    public async Task InstallLocationUsesConfiguredParent()
    {
        var (viewModel, _, settings, _) = await CreateLocationFixture();

        Assert.Equal(Path.Combine(settings.DefaultInstallLocation, "New Game"), viewModel.InstallLocation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedInstallFoldersSanitizeTitlesWithoutChangingTheDisplayTitle(bool chooseParent)
    {
        const string title = "Honkai: Star Rail";
        var (viewModel, _, settings, _) = await CreateLocationFixture(title);
        var parent = settings.DefaultInstallLocation;
        if (chooseParent)
        {
            parent = Path.Combine(_root, "picked-library");
            viewModel.FolderPickerRequested += () => Task.FromResult(parent);
            await viewModel.SelectLocationCommand.ExecuteAsync(null);
        }

        Assert.Equal(title, viewModel.GameTitle);
        Assert.Equal(Path.Combine(parent, "Honkai_ Star Rail"), viewModel.InstallLocation);
        Directory.CreateDirectory(viewModel.InstallLocation);
        Assert.True(Directory.Exists(viewModel.InstallLocation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedInstallRemembersOnlyParentAcrossRestart(bool trailingSeparator)
    {
        var (viewModel, storage, settings, installer) = await CreateLocationFixture();
        var parent = Path.Combine(_root, "chosen-library");
        viewModel.InstallLocation = Path.Combine(parent, "New Game") + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : string.Empty);
        var dlc = CreateGame("dlc", "DLC", "box");
        storage.SaveMetaData(dlc);
        viewModel.AvailableDlcs.Add(new DlcOption { AppName = "dlc", Title = "DLC", IsSelected = true });

        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(parent, settings.DefaultInstallLocation);
        var reloaded = CreateSettings(storage);
        Assert.Equal(parent, reloaded.DefaultInstallLocation);
        Assert.Equal(viewModel.InstallLocation, installer.GameGameInQueue("new").Location);
        Assert.Equal(viewModel.InstallLocation, installer.GameGameInQueue("dlc").Location);
    }

    [Fact]
    public async Task ConfirmedInstallCanRememberFilesystemRoot()
    {
        var (viewModel, storage, _, _) = await CreateLocationFixture();
        var parent = Path.GetPathRoot(_root)!;
        viewModel.InstallLocation = Path.Combine(parent, "New Game");

        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(parent, CreateSettings(storage).DefaultInstallLocation);
    }

    [Fact]
    public async Task ClosingOrCancellingPickerDoesNotRememberEditedLocation()
    {
        var (viewModel, storage, settings, _) = await CreateLocationFixture();
        var original = settings.DefaultInstallLocation;
        viewModel.FolderPickerRequested += () => Task.FromResult<string>(null!);
        await viewModel.SelectLocationCommand.ExecuteAsync(null);
        viewModel.InstallLocation = Path.Combine(_root, "not-confirmed", "New Game");
        viewModel.CloseDialogCommand.Execute(null);

        Assert.Equal(original, settings.DefaultInstallLocation);
        Assert.Equal(original, CreateSettings(storage).DefaultInstallLocation);
    }

    [Fact]
    public async Task RejectedInstallDoesNotChangeRememberedParent()
    {
        var (viewModel, storage, settings, installer) = await CreateLocationFixture();
        var original = settings.DefaultInstallLocation;
        installer.AddToQueue(new InstallItem("new", ActionType.Install, viewModel.InstallLocation));
        viewModel.InstallLocation = Path.Combine(_root, "rejected", "New Game");

        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(original, settings.DefaultInstallLocation);
        Assert.Equal(original, CreateSettings(storage).DefaultInstallLocation);
    }

    [Fact]
    public async Task SettingsFailureDoesNotUndoQueuedInstall()
    {
        var (viewModel, _, _, installer) = await CreateLocationFixture();
        var settingsFile = Path.Combine(_root, "settings.json");
        File.Delete(settingsFile);
        Directory.CreateDirectory(settingsFile);
        var errors = new List<OperationSnapshot>();
        installer.OperationError += errors.Add;
        viewModel.InstallLocation = Path.Combine(_root, "chosen", "New Game");

        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.NotNull(installer.GameGameInQueue("new"));
        var error = Assert.Single(errors);
        Assert.Equal("install_folder_save_failed", error.Failure?.Code);
        Assert.False(error.ShowErrorDialog);
        Assert.Equal("install_folder_save_failed", installer.GameGameInQueue("new")?.Notice?.Code);
    }

    [Fact]
    public async Task ClosingCallbackCannotChangeConfirmedInstallSnapshot()
    {
        var (viewModel, _, settings, installer) = await CreateLocationFixture();
        var parent = Path.Combine(_root, "chosen");
        viewModel.InstallLocation = Path.Combine(parent, "New Game");
        viewModel.RequestClose += () => viewModel.InstallLocation = Path.Combine(_root, "obsolete", "Other Game");

        await viewModel.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(parent, "New Game"), installer.GameGameInQueue("new").Location);
        Assert.Equal(parent, settings.DefaultInstallLocation);
    }

    private async Task<(AppInstallDialogViewModel ViewModel, Storage Storage, SettingsManager Settings, InstallManager Installer)> CreateLocationFixture(string title = "New Game")
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var settings = CreateSettings(storage);
        settings.DefaultInstallLocation = Path.Combine(_root, "configured-library");
        await settings.SaveSettings();
        var game = CreateGame("new", title, "box");
        storage.SaveMetaData(game);
        repository.Complete("new", CreateJsonManifest(100, 10));
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
        // Keep the test's confirmed installs queued without starting workers or touching game files.
        typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(installer,
            new InstallItem("paused-test", ActionType.Install, _root) { Status = ActionStatus.Paused });
        var viewModel = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher(), settings);
        await viewModel.InitializeAsync(game);
        return (viewModel, storage, settings, installer);
    }

    [Fact]
    public async Task ConcurrentSettingsSavesPreserveLatestInMemoryFolder()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var settings = CreateSettings(storage);
        var saves = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            settings.DefaultInstallLocation = Path.Combine(_root, $"library-{index}");
            await settings.SaveSettings();
        }));

        await Task.WhenAll(saves);

        Assert.Equal(settings.DefaultInstallLocation, CreateSettings(storage).DefaultInstallLocation);
    }

    [Fact]
    public async Task SelectedDlcsContributeToTotalsAndTheBaseGameSizeIsPopulated()
    {
        var (model, _, _) = await CreateDlcSizeFixture();
        Assert.Equal("10 B", model.BaseGameSize);
        Assert.Equal("600 B", model.TotalDownloadSize);
        Assert.Equal("60 B", model.TotalInstallSize);
        Assert.True(model.CanInstall);

        model.AvailableDlcs.Single(option => option.AppName == "dlc2").IsSelected = false;
        Assert.Equal("300 B", model.TotalDownloadSize);
        Assert.Equal("30 B", model.TotalInstallSize);
        model.AvailableDlcs.Single(option => option.AppName == "dlc1").IsSelected = false;
        Assert.Equal("100 B", model.TotalDownloadSize);
        Assert.Equal("10 B", model.TotalInstallSize);
        Assert.Equal("10 B", model.BaseGameSize);
    }

    [Fact]
    public async Task SelectedDlcSizeImmediatelyUpdatesTheFreeSpaceGate()
    {
        var (model, _, _) = await CreateDlcSizeFixture();
        var generation = typeof(AppInstallDialogViewModel).GetField("_initialization",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model)!;
        generation.GetType().GetField("AvailableFreeSpace")!.SetValue(generation, 25L);
        foreach (var option in model.AvailableDlcs) option.IsSelected = false;
        Assert.True(model.CanInstall);

        model.AvailableDlcs.Single(option => option.AppName == "dlc1").IsSelected = true;
        Assert.Equal("30 B", model.TotalInstallSize);
        Assert.False(model.CanInstall);
        model.AvailableDlcs.Single(option => option.AppName == "dlc1").IsSelected = false;
        Assert.True(model.CanInstall);
    }

    [Fact]
    public async Task MissingSelectedDlcSizesKeepInstallationDisabled()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var game = CreateGame("game", "Game", "box");
        var dlc = CreateGame("dlc", "DLC", "box");
        dlc.Metadata.MainGameItem = new MainGameItem { Id = game.Metadata.Id };
        storage.SaveMetaData(game);
        storage.SaveMetaData(dlc);
        var library = CreateLibrary(storage, repository);
        using var installer = new InstallManager(_logger, library, repository, storage, null!, null!, new AllowInstallPermissionChecker());
        var model = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher());
        repository.Complete("game", CreateJsonManifest(100, 10));
        repository.Fail("dlc", new HttpRequestException("Synthetic unavailable DLC size"));

        await model.InitializeAsync(game);

        Assert.True(model.HasError);
        Assert.False(model.CanInstall);
        Assert.False(model.IsLoadingContent);
    }

    private async Task<(AppInstallDialogViewModel Model, ControlledManifestRepository Repository, Storage Storage)> CreateDlcSizeFixture()
    {
        var repository = new ControlledManifestRepository();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var game = CreateGame("game", "Game", "box");
        storage.SaveMetaData(game);
        foreach (var appName in new[] { "dlc1", "dlc2" })
        {
            var dlc = CreateGame(appName, appName, "box");
            dlc.Metadata.MainGameItem = new MainGameItem { Id = game.Metadata.Id };
            storage.SaveMetaData(dlc);
        }
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, new AllowInstallPermissionChecker());
        var model = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher());
        repository.Complete("game", CreateJsonManifest(100, 10));
        repository.Complete("dlc1", CreateJsonManifest(200, 20));
        repository.Complete("dlc2", CreateJsonManifest(300, 30));
        await model.InitializeAsync(game);
        return (model, repository, storage);
    }

    private SettingsManager CreateSettings(Storage storage) => new(
        storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsManager>.Instance,
        Path.Combine(_root, "games"), Path.Combine(_root, "logs"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LibraryManager CreateLibrary(Storage storage, IStoreRepository repository)
    {
        var auth = new AuthManager(
            _logger,
            storage,
            new TestCredentialProtector(),
            new HttpClient());
        return new LibraryManager(_logger, repository, storage, auth);
    }

    private static Game CreateGame(string appName, string title, string imageUrl) => new()
    {
        AppName = appName,
        AppTitle = title,
        AssetInfos = new AssetInfos
        {
            Windows = new Asset
            {
                AppName = appName,
                BuildVersion = "1.0",
                CatalogItemId = appName,
                Namespace = "test"
            }
        },
        Metadata = new Metadata
        {
            Id = appName,
            KeyImages = [new KeyImage { Type = "DieselGameBox", Url = imageUrl }]
        }
    };

    private static byte[] CreateJsonManifest(int downloadBytes, int writeBytes)
    {
        const string guid = "00000001000000020000000300000004";
        var json = $$"""
            {
              "FileManifestList":[{
                "Filename":"game.exe",
                "FileHash":"{{EncodeDecimalBytes(new byte[20])}}",
                "FileChunkParts":[{
                  "Guid":"{{guid}}",
                  "Offset":"{{EncodeDecimalBytes(BitConverter.GetBytes(0))}}",
                  "Size":"{{EncodeDecimalBytes(BitConverter.GetBytes(writeBytes))}}"
                }]
              }],
              "ChunkHashList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes(0L))}}"},
              "ChunkShaList":{"{{guid}}":"{{new string('0', 40)}}"},
              "DataGroupList":{"{{guid}}":"{{EncodeDecimalBytes([8])}}"},
              "ChunkFilesizeList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes((long)downloadBytes))}}"}
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    private static string EncodeDecimalBytes(IEnumerable<byte> bytes) =>
        string.Concat(bytes.Select(value => value.ToString("D3")));

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class BlockingFirstDispatcher : IUiDispatcher
    {
        private int _calls;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool TryEnqueue(Action callback)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Blocked.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Dispatcher barrier timed out.");
            }
            callback();
            return true;
        }
    }

    private sealed class BlockingVolumeResolver : IFileSystemVolumeResolver
    {
        private int _calls;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public DriveInfo GetVolume(string path)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Blocked.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Volume barrier timed out.");
            }
            return new DriveInfo(Path.GetPathRoot(path)!);
        }
        public bool AreOnSameVolume(string firstPath, string secondPath) => true;
    }

    private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
    {
        public InstallPermissionCheckResult Check(string folderPath) => new(true);
    }

    private sealed class UnusedShortcutManager : IGameShortcutManager
    {
        public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Remove(Game game)
        {
        }
    }

    private sealed class ControlledManifestRepository : IStoreRepository
    {
        private readonly Dictionary<string, TaskCompletionSource<byte[]>> _manifests = new();

        public void Complete(string appName, byte[] manifest) =>
            GetSource(appName).SetResult(manifest);

        public void Fail(string appName, Exception exception) => GetSource(appName).SetException(exception);

        public Task<GetManifestUrlData> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            string platform = "Windows",
            string label = "Live",
            CancellationToken cancellationToken = default) => Task.FromResult(new GetManifestUrlData
            {
                BaseUrls = [],
                ManifestUrls = [appName],
                ManifestHash = string.Empty
            });

        public event Action<string>? ManifestRequested;

        public async Task<byte[]> GetGameManifest(
            GetManifestUrlData urlData,
            CancellationToken cancellationToken = default)
        {
            var appName = urlData.ManifestUrls[0];
            var source = GetSource(appName);
            ManifestRequested?.Invoke(appName);
            return await source.Task.WaitAsync(cancellationToken);
        }

        private TaskCompletionSource<byte[]> GetSource(string appName)
        {
            lock (_manifests)
            {
                if (!_manifests.TryGetValue(appName, out var source))
                {
                    source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _manifests.Add(appName, source);
                }

                return source;
            }
        }

        public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) =>
            throw new NotSupportedException();

        public Task<long?> FetchGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoreProductDetails?> FetchStoreProductDetails(
            string nameSpace,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(
            string query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DownloadFileAsync(string url, string destinationPath) =>
            throw new NotSupportedException();

        public Task<string> GetGameToken() => throw new NotSupportedException();

        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> GetCloudSaveFilesAsync(string appName, bool manifestsOnly, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> CreateCloudSaveFilesAsync(string appName, IReadOnlyList<string> files, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> DownloadCloudSaveFileAsync(string readLink, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UploadCloudSaveFileAsync(string writeLink, byte[] data, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
