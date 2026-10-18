using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class GameDlcViewModelTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Fact]
    public async Task LoadShowsInstalledStateAndLeavesUnknownSizesBlank()
    {
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.True(viewModel.Items.Single(item => item.AppName == "installed").IsSelected);
        Assert.False(viewModel.Items.Single(item => item.AppName == "available").IsSelected);
        Assert.All(viewModel.Items, item => Assert.Equal(string.Empty, item.SizeText));
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.CanApply);
        Assert.False(viewModel.ShowApply);
    }

    [Fact]
    public async Task CheckboxChangesStayStagedUntilApplyAndSurviveSearch()
    {
        _fixture.SetCurrent("unrelated");
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        viewModel.Items.Single(item => item.AppName == "available").IsSelected = true;
        viewModel.Items.Single(item => item.AppName == "installed").IsSelected = false;
        viewModel.SearchText = "Available";

        Assert.Single(viewModel.Items);
        Assert.True(viewModel.CanApply);
        Assert.Empty(_fixture.Installer.GetQueueOperations());
        var plan = viewModel.GetChangePlan();
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, item => item.AppName == "available" && item.Action == ActionType.Install);
        Assert.Contains(plan, item => item.AppName == "installed" && item.Action == ActionType.Uninstall);
        Assert.All(plan, item => Assert.Equal(_fixture.BasePath, item.Location));

        viewModel.ApplyChanges();

        Assert.Equal(2, _fixture.Installer.GetQueueOperations().Count);
    }

    [Fact]
    public async Task ReturningCheckboxToOriginalStateClearsPendingChanges()
    {
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        var item = viewModel.Items.Single(row => row.AppName == "available");
        item.IsSelected = true;
        Assert.True(viewModel.HasChanges);
        Assert.True(viewModel.ShowApply);
        viewModel.IsConfirmingRemoval = true;
        Assert.False(viewModel.ShowApply);
        viewModel.IsConfirmingRemoval = false;
        Assert.True(viewModel.ShowApply);
        item.IsSelected = false;

        Assert.False(viewModel.HasChanges);
        Assert.False(viewModel.CanApply);
        Assert.False(viewModel.ShowApply);
        Assert.Empty(viewModel.GetChangePlan());
    }

    [Fact]
    public async Task RelatedOperationDisablesChangesAndApplyRechecksCurrentState()
    {
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        viewModel.Items.Single(item => item.AppName == "available").IsSelected = true;
        _fixture.SetCurrent("installed");

        Assert.Throws<InvalidOperationException>(() => viewModel.ApplyChanges());
        Assert.False(viewModel.CanApply);
        Assert.All(viewModel.Items, item => Assert.False(item.CanEdit));
        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Fact]
    public async Task RemovalsUseTheDlcTrackedPathRatherThanAssumingTheBasePath()
    {
        _fixture.InstalledDlc.LocalAppState!.InstallPath = Path.Combine(_fixture.Root, "separate-dlc");
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        viewModel.Items.Single(item => item.AppName == "installed").IsSelected = false;

        var plan = Assert.Single(viewModel.GetChangePlan());
        Assert.Equal(_fixture.InstalledDlc.LocalAppState.InstallPath, plan.Location);
    }

    [Fact]
    public void ChangePlanRejectsUnknownDlcAndMissingBaseInstallation()
    {
        var choice = new GameDlcItem { AppName = "unknown", Title = "Unknown", IsSelected = true };
        Assert.Throws<InvalidOperationException>(() =>
            GameDlcViewModel.CreateChangePlan(_fixture.BaseGame, [choice], []));
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        Assert.Throws<InvalidOperationException>(() =>
            GameDlcViewModel.CreateChangePlan(_fixture.BaseGame, [], []));
    }

    [Fact]
    public async Task CachedManifestSuppliesSizeWithoutNetworkRequests()
    {
        await _fixture.Cache("installed", "dlc-build");
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);

        Assert.Equal("0.00 MiB", viewModel.Items.Single(item => item.AppName == "installed").SizeText);
        Assert.Equal(string.Empty, viewModel.Items.Single(item => item.AppName == "available").SizeText);
    }

    [Fact]
    public async Task RecordedSizeAboveTwoGiBDoesNotRequireCachedManifest()
    {
        _fixture.InstalledDlc.LocalAppState!.InstallSize = 5L * 1024 * 1024 * 1024;

        Assert.Equal(5L * 1024 * 1024 * 1024, await _fixture.Installer.GetCachedInstallSizeBytes("installed"));
        var json = JsonSerializer.Serialize(_fixture.InstalledDlc.LocalAppState);
        Assert.Equal(5L * 1024 * 1024 * 1024, JsonSerializer.Deserialize<LocalAppState>(json)!.InstallSize);
    }

    [Fact]
    public void ManifestStateRecordsFullInstalledSizeAs64BitBytes()
    {
        var manifest = new Manifest
        {
            ManifestMeta = new ManifestMeta(),
            FileManifestList = new FileManifestList()
        };
        manifest.FileManifestList.Elements.Add(new FileManifest { Filename = "one.bin", FileSize = 3L * 1024 * 1024 * 1024 });
        manifest.FileManifestList.Elements.Add(new FileManifest { Filename = "two.bin", FileSize = 2L * 1024 * 1024 * 1024 });
        var method = typeof(InstallManager).GetMethod("ApplyManifestState", BindingFlags.NonPublic | BindingFlags.Static)!;

        method.Invoke(null, [new InstallItem("installed", ActionType.Install, _fixture.BasePath),
            _fixture.InstalledDlc, _fixture.InstalledDlc.LocalAppState!, manifest]);

        Assert.Equal(5L * 1024 * 1024 * 1024, _fixture.InstalledDlc.LocalAppState!.InstallSize);
    }

    [Fact]
    public async Task ClosingDiscardsQueuedSizeResults()
    {
        await _fixture.Cache("installed", "dlc-build");
        var dispatcher = new QueuedDispatcher();
        var viewModel = _fixture.ViewModel(dispatcher);
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        var item = viewModel.Items.Single(row => row.AppName == "installed");
        viewModel.Close();
        dispatcher.Flush();

        Assert.Equal(string.Empty, item.SizeText);
    }

    [Fact]
    public async Task DlcListExcludesCachedItemsNotOwnedByTheCurrentAccount()
    {
        _fixture.SetOwned("base", "available");
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);

        Assert.Equal("available", Assert.Single(viewModel.Items).AppName);
    }

    [Fact]
    public async Task ApplyRechecksOwnershipBeforeQueueingChanges()
    {
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        viewModel.Items.Single(item => item.AppName == "available").IsSelected = true;
        _fixture.SetOwned("base", "installed");

        Assert.Throws<InvalidOperationException>(() => viewModel.ApplyChanges());
        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Fact]
    public async Task SearchWithoutMatchesHasAnExplicitEmptyState()
    {
        var viewModel = _fixture.ViewModel();
        await viewModel.LoadAsync(_fixture.BaseGame, CancellationToken.None);
        viewModel.SearchText = "not a DLC title";
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("No matching DLC.", viewModel.EmptyMessage);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _callbacks = new();
        public bool TryEnqueue(Action callback) { _callbacks.Enqueue(callback); return true; }
        public void Flush() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }
}

public sealed class DlcUninstallTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Fact]
    public async Task RemovingDlcKeepsBaseAndSiblingFiles()
    {
        await _fixture.Cache("base", "base-build", "shared.bin", "base.bin");
        _fixture.Storage.GameMetaDataDictionary["available"].LocalAppState!.InstallStatus = InstallState.Installed;
        _fixture.Storage.GameMetaDataDictionary["available"].LocalAppState!.Version = "sibling-build";
        await _fixture.Cache("available", "sibling-build", "sibling-shared.bin");
        var target = Manifest.ReadAll(DlcTestFixture.ManifestBytes("shared.bin", "sibling-shared.bin", "dlc-only.bin"));
        var install = new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath);

        await _fixture.Invoke("PrepareDlcUninstallTasks", install, target);

        var task = Assert.Single(_fixture.IoTasks());
        Assert.Equal(Path.Combine(_fixture.BasePath, "dlc-only.bin"), task.DestinationFilePath);
    }

    [Fact]
    public async Task MissingProtectedManifestBlocksRemovalBeforeAnyDeletionIsQueued()
    {
        var target = Manifest.ReadAll(DlcTestFixture.ManifestBytes("dlc-only.bin"));
        var install = new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _fixture.Invoke("PrepareDlcUninstallTasks", install, target));
        Assert.Empty(_fixture.IoTasks());
    }

    [Fact]
    public async Task DlcRemovalUsesInstalledManifestInsteadOfLatestCatalogManifest()
    {
        await _fixture.Cache("base", "base-build", "shared.bin");
        await _fixture.Cache("installed", "dlc-build", "shared.bin", "old-dlc.bin");
        _fixture.Queue().Add(new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath));

        await _fixture.Invoke("PrepareTasks", false, null!);

        var task = Assert.Single(_fixture.IoTasks());
        Assert.Equal(Path.Combine(_fixture.BasePath, "old-dlc.bin"), task.DestinationFilePath);
    }

    [Fact]
    public async Task TraversalInDlcManifestBlocksAllDeletionTasks()
    {
        await _fixture.Cache("base", "base-build");
        var target = Manifest.ReadAll(DlcTestFixture.ManifestBytes("valid.bin", "../outside.bin"));

        await Assert.ThrowsAsync<InvalidDataException>(() => _fixture.Invoke("PrepareDlcUninstallTasks",
            new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath), target));
        Assert.Empty(_fixture.IoTasks());
    }

    [Fact]
    public async Task DlcRemovalPreservesNestedInstalledContent()
    {
        await _fixture.Cache("base", "base-build");
        var sibling = _fixture.Storage.GameMetaDataDictionary["available"].LocalAppState!;
        sibling.InstallStatus = InstallState.Installed;
        sibling.Version = "sibling-build";
        sibling.InstallPath = Path.Combine(_fixture.BasePath, "nested");
        Directory.CreateDirectory(sibling.InstallPath);
        await _fixture.Cache("available", "sibling-build", "shared.bin");
        var target = Manifest.ReadAll(DlcTestFixture.ManifestBytes("nested/shared.bin", "dlc-only.bin"));

        await _fixture.Invoke("PrepareDlcUninstallTasks", new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath), target);

        var task = Assert.Single(_fixture.IoTasks());
        Assert.Equal(Path.Combine(_fixture.BasePath, "dlc-only.bin"), task.DestinationFilePath);
    }

    public void Dispose() => _fixture.Dispose();
}

internal sealed class DlcTestFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"crimson-dlc-management-{Guid.NewGuid():N}");
    public string BasePath => Path.Combine(Root, "game");
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly HttpClient _http = new();
    private readonly List<GameDlcViewModel> _viewModels = [];
    public Storage Storage { get; }
    public LibraryManager Library { get; }
    public InstallManager Installer { get; }
    public Game BaseGame { get; }
    public Game InstalledDlc { get; }

    public DlcTestFixture()
    {
        Directory.CreateDirectory(BasePath);
        Storage = new Storage(_logger, Path.Combine(Root, "data"), BasePath);
        BaseGame = Game("base", null, InstallState.Installed, "base-build");
        InstalledDlc = Game("installed", "base-id", InstallState.Installed, "dlc-build");
        var available = Game("available", "base-id", InstallState.NotInstalled, null);
        foreach (var game in new[] { BaseGame, InstalledDlc, available })
        {
            Storage.SaveMetaData(game);
            Storage.AddToLocalAppState(game.AppName, game.LocalAppState!);
        }
        var repository = new NoNetworkRepository();
        Library = new LibraryManager(_logger, repository, Storage, new AuthManager(_logger, Storage, new TestCredentialProtector(), _http));
        Installer = new InstallManager(_logger, Library, repository, Storage, null!, null!, new AllowPermission());
        SetOwned("base", "installed", "available");
    }

    public void SetOwned(params string[] appNames)
    {
        typeof(LibraryManager).GetField("_ownedAppNames", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(Library, appNames.ToHashSet(StringComparer.Ordinal));
        typeof(LibraryManager).GetField("_ownedAccountGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(Library, Storage.AccountGeneration);
    }

    public GameDlcViewModel ViewModel(IUiDispatcher? dispatcher = null)
    {
        var viewModel = new GameDlcViewModel(Library, Installer, dispatcher ?? new ImmediateDispatcher());
        _viewModels.Add(viewModel);
        return viewModel;
    }

    public void SetCurrent(string appName) => typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!
        .SetValue(Installer, new InstallItem(appName, ActionType.Install, BasePath) { Status = ActionStatus.Processing });

    public List<InstallItem> Queue() => (List<InstallItem>)typeof(InstallManager)
        .GetField("_installQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Installer)!;

    public IoTask[] IoTasks() => ((BlockingCollection<IoTask>)typeof(InstallManager)
        .GetField("_ioQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Installer)!).ToArray();

    public Task Invoke(string method, params object[] args) => (Task)typeof(InstallManager)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Installer, args)!;

    public Task Cache(string appName, string version, params string[] files) =>
        Storage.CacheManifestBytes(appName, version, ManifestBytes(files));

    public static byte[] ManifestBytes(params string[] files) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        FileManifestList = files.Select(file => new { Filename = file, FileHash = new string('0', 60), FileChunkParts = Array.Empty<object>() }),
        ChunkHashList = new Dictionary<string, string>(),
        ChunkShaList = new Dictionary<string, string>(),
        DataGroupList = new Dictionary<string, string>(),
        ChunkFilesizeList = new Dictionary<string, string>()
    }));

    private Game Game(string appName, string? mainGameId, InstallState state, string? version) => new()
    {
        AppName = appName,
        AppTitle = appName == "available" ? "Available DLC" : appName,
        AssetInfos = new AssetInfos { Windows = new Asset { AppName = appName, CatalogItemId = appName + "-id", Namespace = "synthetic", BuildVersion = "latest-build" } },
        Metadata = new Metadata { Id = appName + "-id", KeyImages = [], MainGameItem = mainGameId is null ? null! : new MainGameItem { Id = mainGameId } },
        LocalAppState = new LocalAppState { AppName = appName, InstallStatus = state, InstallPath = BasePath, Version = version! }
    };

    public void Dispose()
    {
        foreach (var viewModel in _viewModels) viewModel.Close();
        _http.Dispose();
        _logger.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

    private sealed class AllowPermission : IInstallPermissionChecker
    {
        public InstallPermissionCheckResult Check(string folderPath) => new(true);
    }

    private sealed class NoNetworkRepository : IStoreRepository
    {
        public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<StoreProductDetails?> FetchStoreProductDetails(string nameSpace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> FetchGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
        public Task<string> GetGameToken() => throw new NotSupportedException();
        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
