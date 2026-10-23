using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class ImportFlowTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrParentFolderFailsWithoutRegisteringAnInstallation(bool containsNestedGame)
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        game.LocalAppState.InstallPath = null!;
        await CacheImportManifest(game, "game.exe", "data.bin");
        var selected = Path.Combine(_fixture.Root, "selected");
        Directory.CreateDirectory(selected);
        if (containsNestedGame)
        {
            var nested = Path.Combine(selected, "game");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "game.exe"), "synthetic data");
        }
        var item = new InstallItem(game.AppName, ActionType.Import, selected);

        await RunToTerminal(item);

        Assert.Equal(ActionStatus.Failed, item.Status);
        Assert.Equal("import_location_invalid", item.Failure?.Code);
        Assert.False(item.Failure!.CanRetry);
        Assert.Equal(InstallState.NotInstalled, game.LocalAppState.InstallStatus);
        Assert.Null(game.LocalAppState.InstallPath);
        Assert.Empty(_fixture.Library.GetSidebarGames([game], null));
    }

    [Fact]
    public async Task InvalidFolderPublishesReportableErrorAndSuccessfulRetryDoesNotRepeatIt()
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        await CacheImportManifest(game, "game.exe");
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;
        var failed = new InstallItem("base", ActionType.Import, _fixture.BasePath);

        await RunToTerminal(failed);

        var error = Assert.Single(errors);
        Assert.Equal(ActionType.Import, error.Action);
        Assert.Equal("import_location_invalid", error.Failure?.Code);
        Assert.Equal("No game files were found in the selected folder.", error.Failure?.Message);
        Assert.Equal("Choose the folder containing the game files, then import again.", error.Failure?.SuggestedAction);
        File.WriteAllText(Path.Combine(_fixture.BasePath, "game.exe"), "synthetic game");
        var retry = new InstallItem("base", ActionType.Import, _fixture.BasePath);

        await RunToTerminal(retry);

        Assert.Equal(ActionStatus.Success, retry.Status);
        Assert.Single(errors);
    }

    [Fact]
    public async Task FailedImportPreservesAnExistingInstallationRecord()
    {
        var game = _fixture.BaseGame;
        var originalPath = game.LocalAppState!.InstallPath;
        var originalVersion = game.LocalAppState.Version;
        await CacheImportManifest(game, "game.exe");
        var selected = Path.Combine(_fixture.Root, "empty");
        Directory.CreateDirectory(selected);
        var item = new InstallItem(game.AppName, ActionType.Import, selected);

        await RunToTerminal(item);

        Assert.Equal(ActionStatus.Failed, item.Status);
        Assert.Equal(InstallState.Installed, game.LocalAppState.InstallStatus);
        Assert.Equal(originalPath, game.LocalAppState.InstallPath);
        Assert.Equal(originalVersion, game.LocalAppState.Version);
    }

    [Fact]
    public async Task ImportRejectsManifestTraversalWithoutRegisteringTheSelectedFolder()
    {
        var item = new InstallItem("base", ActionType.Import, _fixture.BasePath);
        var manifest = Manifest.ReadAll(DlcTestFixture.ManifestBytes("../outside.bin"));
        var original = _fixture.BaseGame.LocalAppState;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _fixture.Invoke("PrepareImportTasks", item, manifest));

        Assert.Same(original, _fixture.BaseGame.LocalAppState);
        Assert.Null(typeof(InstallManager).GetField("_importVerificationResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_fixture.Installer));
    }

    [Fact]
    public async Task CachedFullImportPublishesInstalledStateAndAppearsInSidebar()
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        await CacheImportManifest(game, "game.exe");
        File.WriteAllText(Path.Combine(_fixture.BasePath, "game.exe"), "synthetic game");
        var published = new List<InstallState>();
        _fixture.Library.GameStatusUpdated += updated => published.Add(updated.LocalAppState!.InstallStatus);
        var item = new InstallItem(game.AppName, ActionType.Import, _fixture.BasePath);

        await RunToTerminal(item);

        // The fixture repository rejects network requests, including redundant manifest URL requests.
        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(InstallState.Installed, game.LocalAppState.InstallStatus);
        Assert.Equal("latest-build", game.LocalAppState.Version);
        Assert.Contains(InstallState.Installed, published);
        Assert.Same(game, Assert.Single(_fixture.Library.GetSidebarGames([game], null)));
        Assert.Equal("Verified 1 files", item.StatusMessage);
    }

    [Fact]
    public async Task PartialImportRemainsVisibleAsRepairableRatherThanReady()
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        await CacheImportManifest(game, "game.exe", "missing.bin");
        File.WriteAllText(Path.Combine(_fixture.BasePath, "game.exe"), "synthetic game");
        var item = new InstallItem(game.AppName, ActionType.Import, _fixture.BasePath);

        await RunToTerminal(item);

        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(InstallState.Broken, game.LocalAppState.InstallStatus);
        Assert.Same(game, Assert.Single(_fixture.Library.GetSidebarGames([game], null)));
        Assert.Equal("Verified 2 files: 1 require repair", item.StatusMessage);
        Assert.Null(game.LocalAppState.Version);
    }

    [Theory]
    [InlineData("synthetic gome")]
    [InlineData("short")]
    public async Task CorruptOrOlderImportedFilesRequireRepairAndDoNotClaimLatestBuild(string content)
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        await CacheImportManifest(game, "game.exe");
        File.WriteAllText(Path.Combine(_fixture.BasePath, "game.exe"), content);
        var item = new InstallItem(game.AppName, ActionType.Import, _fixture.BasePath);

        await RunToTerminal(item);

        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(InstallState.Broken, game.LocalAppState.InstallStatus);
        Assert.Null(game.LocalAppState.Version);
        Assert.Equal("Verified 1 files: 1 require repair", item.StatusMessage);
    }

    [Fact]
    public async Task CatalogRefreshCannotRelabelTheBuildThatWasActuallyVerified()
    {
        var game = _fixture.BaseGame;
        game.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        File.WriteAllText(Path.Combine(_fixture.BasePath, "game.exe"), "synthetic game");
        var json = CreateImportManifest(["game.exe"]);
        json["BuildVersionString"] = "verified-build";
        var manifest = Manifest.ReadAll(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var item = new InstallItem(game.AppName, ActionType.Import, _fixture.BasePath);
        await _fixture.Invoke("PrepareImportTasks", item, manifest);
        game.AssetInfos.Windows.BuildVersion = "newer-build";

        await _fixture.Invoke("CompleteImportAsync", item, game, game.LocalAppState);

        Assert.Equal("verified-build", game.LocalAppState.Version);
        Assert.Equal(InstallState.NeedUpdate, game.LocalAppState.InstallStatus);
    }

    [Fact]
    public async Task FailedImportDoesNotBlockTheNextQueuedOperation()
    {
        var firstGame = _fixture.BaseGame;
        firstGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        var nextGame = _fixture.InstalledDlc;
        nextGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        await CacheImportManifest(firstGame, "absent.bin");
        await CacheImportManifest(nextGame, "dlc.bin");
        File.WriteAllText(Path.Combine(_fixture.BasePath, "dlc.bin"), "synthetic game");
        var first = new InstallItem(firstGame.AppName, ActionType.Import, _fixture.BasePath);
        var next = new InstallItem(nextGame.AppName, ActionType.Import, _fixture.BasePath);
        var nextCompleted = Terminal(next);

        _fixture.Installer.AddToQueue(first);
        _fixture.Installer.AddToQueue(next);
        await nextCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTeardown(next).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.Failed, first.Status);
        Assert.Equal(ActionStatus.Success, next.Status);
        Assert.Equal(InstallState.Installed, nextGame.LocalAppState.InstallStatus);
    }

    [Fact]
    public async Task CancelledImportScanDoesNotRegisterAResult()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var manifest = Manifest.ReadAll(DlcTestFixture.ManifestBytes("game.exe"));
        var method = typeof(InstallManager).GetMethod("VerifyImportFilesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var item = new InstallItem("base", ActionType.Import, _fixture.BasePath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            (Task)method.Invoke(null, [item, manifest, cancelled.Token])!);
        Assert.Null(typeof(InstallManager).GetField("_importVerificationResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_fixture.Installer));
    }

    private async Task CacheImportManifest(Game game, params string[] files)
    {
        game.BaseUrls = [];
        game.LocalAppState!.CachedManifestVersion = "latest-build";
        var json = CreateImportManifest(files);
        json["BuildVersionString"] = "latest-build";
        json["LaunchExeString"] = "game.exe";
        await _fixture.Storage.CacheManifestBytes(game.AppName, "latest-build", Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    internal static JsonObject CreateImportManifest(string[] files)
    {
        const string guid = "00000001000000020000000300000004";
        var contents = Encoding.UTF8.GetBytes("synthetic game");
        static string Encode(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("D3")));
        var json = JsonNode.Parse(DlcTestFixture.ManifestBytes(files))!.AsObject();
        foreach (var file in json["FileManifestList"]!.AsArray())
        {
            file!["FileHash"] = Encode(SHA1.HashData(contents));
            file["FileChunkParts"] = new JsonArray(new JsonObject
            {
                ["Guid"] = guid,
                ["Offset"] = Encode(BitConverter.GetBytes(0)),
                ["Size"] = Encode(BitConverter.GetBytes(contents.Length))
            });
        }
        json["ChunkHashList"] = new JsonObject { [guid] = Encode(new byte[8]) };
        json["ChunkShaList"] = new JsonObject { [guid] = new string('0', 40) };
        json["DataGroupList"] = new JsonObject { [guid] = Encode([0]) };
        json["ChunkFilesizeList"] = new JsonObject { [guid] = Encode(BitConverter.GetBytes((long)contents.Length)) };
        return json;
    }

    private async Task RunToTerminal(InstallItem item)
    {
        var completed = Terminal(item);
        _fixture.Installer.AddToQueue(item);
        await completed.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForTeardown(item).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task WaitForTeardown(InstallItem item)
    {
        while (ReferenceEquals(_fixture.Installer.CurrentInstall, item))
            await Task.Delay(1);
    }

    private Task Terminal(InstallItem item)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Installer.InstallationStatusChanged += updated =>
        {
            if (ReferenceEquals(updated, item) && updated.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                completed.TrySetResult();
        };
        return completed.Task;
    }

    public void Dispose() => _fixture.Dispose();
}

public sealed class SidebarImportStateTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Theory]
    [InlineData(InstallState.NotInstalled, false)]
    [InlineData(InstallState.Installed, true)]
    [InlineData(InstallState.Broken, true)]
    [InlineData(InstallState.NeedUpdate, true)]
    [InlineData(InstallState.Playing, true)]
    [InlineData(InstallState.Installing, true)]
    [InlineData(InstallState.InstallationPaused, true)]
    [InlineData(InstallState.Updating, true)]
    [InlineData(InstallState.UpdatingPaused, true)]
    [InlineData(InstallState.Repairing, true)]
    public void SidebarIncludesInstalledAndRepairableStates(InstallState state, bool expected)
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = state;
        Assert.Equal(expected, LibraryManager.IsSidebarListed(_fixture.BaseGame, null));
    }

    [Fact]
    public void SidebarResolvesCurrentStoredStateInsteadOfStaleCachedReferences()
    {
        var stale = new Game
        {
            AppName = "base", AppTitle = "Stale library entry", AssetInfos = _fixture.BaseGame.AssetInfos,
            Metadata = _fixture.BaseGame.Metadata,
            LocalAppState = new LocalAppState { AppName = "base", InstallStatus = InstallState.NotInstalled }
        };
        _fixture.BaseGame.LocalAppState = stale.LocalAppState;
        var actual = new LocalAppState { AppName = "base", InstallStatus = InstallState.Installed, InstallPath = _fixture.BasePath };
        _fixture.Storage.AddToLocalAppState("base", actual);

        var displayed = Assert.Single(_fixture.Library.GetSidebarGames([stale], null));

        Assert.Same(_fixture.BaseGame, displayed);
        Assert.Same(actual, displayed.LocalAppState);
    }

    [Fact]
    public void ActiveImportIsListedBeforeItHasAnInstalledStateButDlcStaysHidden()
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        Assert.Single(_fixture.Library.GetSidebarGames([_fixture.BaseGame, _fixture.InstalledDlc], "base"));
    }

    public void Dispose() => _fixture.Dispose();
}
