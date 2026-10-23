using System.Reflection;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class InstallationCleanupTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Fact]
    public async Task CancellingNewInstallRemovesPartialFilesChunksAndEmptyRoot()
    {
        var install = await NewInstall("bin/game.exe", "data/content.bin");
        Write(install.Location, "bin/game.exe");
        Write(install.Location, "data/content.bin");
        Write(install.Location, ".Crimson/123.chunk");
        Write(install.Location, ".Crimson/456.chunk.partial");

        await Cancel(install);

        Assert.False(Directory.Exists(install.Location));
        Assert.Equal(ActionStatus.Cancelled, install.Status);
        Assert.Null(install.Notice);
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task CancellingPartialInstallSkipsFilesWhoseDirectoriesWereNeverCreated()
    {
        var install = await NewInstall("not-downloaded/game.bin", "content/fr/Drawful2Prompt.jet");
        Write(install.Location, "content/fr/Drawful2Prompt.jet");

        await Cancel(install);

        Assert.Null(install.Notice);
        Assert.False(Directory.Exists(install.Location));
        Assert.Equal(ActionStatus.Cancelled, install.Status);
    }

    [Theory]
    [InlineData("missing.bin")]
    [InlineData("never-created/missing.bin")]
    public void ManifestFileDeletionAllowsAlreadyMissingFilesAndDirectories(string relativePath)
    {
        InstallPathPolicy.DeleteFile(_fixture.BasePath, Path.Combine(_fixture.BasePath, relativePath));

        Assert.True(Directory.Exists(_fixture.BasePath));
    }

    [Fact]
    public async Task CancellationPreservesUntrackedFilesIncludingFilesInTheTemporaryFolder()
    {
        var install = await NewInstall("bin/game.exe");
        Write(install.Location, "bin/game.exe");
        Write(install.Location, "saves/player.sav");
        Write(install.Location, ".Crimson/notes.txt");
        Write(install.Location, ".Crimson/123.chunk");

        await Cancel(install);

        Assert.False(Directory.Exists(Path.Combine(install.Location, "bin")));
        Assert.True(File.Exists(Path.Combine(install.Location, "saves/player.sav")));
        Assert.True(File.Exists(Path.Combine(install.Location, ".Crimson/notes.txt")));
        Assert.False(File.Exists(Path.Combine(install.Location, ".Crimson/123.chunk")));
        Assert.Null(install.Notice);
    }

    [Fact]
    public async Task CancellingBeforeManifestPreparationRemovesAnEmptyDestination()
    {
        var location = Path.Combine(_fixture.Root, "new-game");
        Directory.CreateDirectory(location);
        var install = new InstallItem("available", ActionType.Install, location);

        await Cancel(install);

        Assert.False(Directory.Exists(location));
        Assert.Null(install.Notice);
    }

    [Fact]
    public async Task CancellationWaitsForFileWorkersBeforeCleaningUp()
    {
        var install = await NewInstall("game.exe");
        var path = Write(install.Location, "game.exe");
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField("_installTasks", new List<Task> { worker.Task });

        var cancellation = Cancel(install);
        try
        {
            Assert.False(cancellation.IsCompleted);
            Assert.True(File.Exists(path));
        }
        finally
        {
            worker.SetResult();
        }
        await cancellation;

        Assert.False(Directory.Exists(install.Location));
    }

    [Theory]
    [InlineData(ActionType.Update)]
    [InlineData(ActionType.Repair)]
    [InlineData(ActionType.Verify)]
    [InlineData(ActionType.Import)]
    [InlineData(ActionType.Move)]
    [InlineData(ActionType.Uninstall)]
    [InlineData(ActionType.Install)]
    public async Task CancellingOtherOperationsPreservesExistingGameFiles(ActionType action)
    {
        var file = Write(_fixture.BasePath, "game.exe");
        var install = new InstallItem("base", action, _fixture.BasePath);

        await Cancel(install);

        Assert.True(File.Exists(file));
        Assert.Null(install.Notice);
    }

    [Fact]
    public async Task CancellingDlcInstallPreservesSharedFilesAndRemovesExclusiveFiles()
    {
        await _fixture.Cache("base", "base-build", "shared.bin");
        await _fixture.Cache("installed", "dlc-build", "sibling.bin");
        var install = await NewInstall("shared.bin", "sibling.bin", "addon/content.bin");
        install.Location = _fixture.BasePath;
        var shared = Write(install.Location, "shared.bin");
        var sibling = Write(install.Location, "sibling.bin");
        Write(install.Location, "addon/content.bin");

        await Cancel(install);

        Assert.True(File.Exists(shared));
        Assert.True(File.Exists(sibling));
        Assert.False(Directory.Exists(Path.Combine(install.Location, "addon")));
        Assert.Equal(InstallState.Installed, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.Null(install.Notice);
    }

    [Fact]
    public async Task MissingProtectedManifestPreservesPartialFilesAndReportsCleanupFailure()
    {
        var install = await NewInstall("game.exe");
        install.Location = _fixture.BasePath;
        var file = Write(install.Location, "game.exe");

        await Cancel(install);

        Assert.True(File.Exists(file));
        Assert.Equal("cancelled_install_cleanup_failed", install.Notice?.Code);
        Assert.Equal(ActionStatus.Cancelled, install.Status);
    }

    [Fact]
    public async Task TraversalInCancellationManifestBlocksAllGameFileDeletion()
    {
        var install = await NewInstall("game.exe", "../outside.bin");
        var file = Write(install.Location, "game.exe");
        var outside = Write(_fixture.Root, "outside.bin");

        await Cancel(install);

        Assert.True(File.Exists(file));
        Assert.True(File.Exists(outside));
        Assert.Equal("cancelled_install_cleanup_failed", install.Notice?.Code);
    }

    [Fact]
    public async Task UnavailableCancellationManifestPreservesFilesAndReportsCleanupFailure()
    {
        var install = await NewInstall("game.exe");
        var file = Write(install.Location, "game.exe");
        _fixture.Storage.LocalAppStateDictionary[install.AppName].CachedManifestVersion = "missing-build";

        await Cancel(install);

        Assert.True(File.Exists(file));
        Assert.Equal("cancelled_install_cleanup_failed", install.Notice?.Code);
    }

    [Fact]
    public async Task FileCleanupFailureRemainsVisibleAndPreservesUnexpectedDirectoryContents()
    {
        var install = await NewInstall("game.exe");
        var unexpected = Write(install.Location, "game.exe/save.dat");

        await Cancel(install);

        Assert.True(File.Exists(unexpected));
        Assert.Equal("cancelled_install_cleanup_failed", install.Notice?.Code);
        Assert.Equal(ActionStatus.Cancelled, install.Status);
    }

    [Fact]
    public async Task BaseUninstallProtectsFilesOwnedByAnotherGameInTheSameFolder()
    {
        var state = new LocalAppState
        {
            AppName = "other-game",
            InstallStatus = InstallState.Installed,
            InstallPath = _fixture.BasePath,
            Version = "other-build"
        };
        _fixture.Storage.AddToLocalAppState(state.AppName, state);
        await _fixture.Cache(state.AppName, state.Version, "shared.bin");
        await _fixture.Cache("installed", "dlc-build", "addon.bin");
        var manifest = Manifest.ReadAll(DlcTestFixture.ManifestBytes("shared.bin", "base.bin"));
        var install = new InstallItem("base", ActionType.Uninstall, _fixture.BasePath);

        await _fixture.Invoke("PrepareBaseUninstallTasks", install, _fixture.BaseGame, manifest);

        Assert.Equal(2, _fixture.IoTasks().Length);
        Assert.DoesNotContain(_fixture.IoTasks(), task =>
            task.DestinationFilePath == Path.Combine(_fixture.BasePath, "shared.bin"));
    }

    [Fact]
    public void CompletedBaseUninstallRemovesTemporaryFilesAndEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_fixture.BasePath, "bin/nested"));
        Write(_fixture.BasePath, ".Crimson/123.chunk");
        var install = new InstallItem("base", ActionType.Uninstall, _fixture.BasePath);

        CompleteUninstall(install, _fixture.BaseGame);

        Assert.False(Directory.Exists(_fixture.BasePath));
        Assert.True(Directory.Exists(_fixture.Root));
        Assert.Equal(InstallState.NotInstalled, _fixture.InstalledDlc.LocalAppState!.InstallStatus);
    }

    [Fact]
    public void CompletedUninstallPreservesUntrackedFilesButRemovesEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_fixture.BasePath, "bin/nested"));
        var save = Write(_fixture.BasePath, "saves/player.sav");
        var install = new InstallItem("base", ActionType.Uninstall, _fixture.BasePath);

        CompleteUninstall(install, _fixture.BaseGame);

        Assert.False(Directory.Exists(Path.Combine(_fixture.BasePath, "bin")));
        Assert.True(File.Exists(save));
        Assert.Equal(InstallState.NotInstalled, _fixture.BaseGame.LocalAppState!.InstallStatus);
    }

    [Fact]
    public void DlcUninstallPreservesEmptyRootsBelongingToInstalledGames()
    {
        var install = new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath);

        CompleteUninstall(install, _fixture.InstalledDlc);

        Assert.True(Directory.Exists(_fixture.BasePath));
        Assert.Equal(InstallState.Installed, _fixture.BaseGame.LocalAppState!.InstallStatus);
    }

    [Fact]
    public void UninstallPreservesNestedRootsOfOtherInstalledGames()
    {
        var other = _fixture.Storage.LocalAppStateDictionary["available"];
        other.InstallStatus = InstallState.Installed;
        other.InstallPath = Path.Combine(_fixture.BasePath, "other-game");
        Directory.CreateDirectory(other.InstallPath);
        var install = new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath);

        CompleteUninstall(install, _fixture.InstalledDlc);

        Assert.True(Directory.Exists(other.InstallPath));
        Assert.True(Directory.Exists(_fixture.BasePath));
    }

    private async Task<InstallItem> NewInstall(params string[] files)
    {
        const string version = "new-build";
        await _fixture.Cache("available", version, files);
        _fixture.Storage.LocalAppStateDictionary["available"].CachedManifestVersion = version;
        var location = Path.Combine(_fixture.Root, "new-game");
        Directory.CreateDirectory(location);
        return new InstallItem("available", ActionType.Install, location);
    }

    private Task Cancel(InstallItem install)
    {
        typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(_fixture.Installer, install);
        SetField("_userCancelled", true);
        return _fixture.Invoke("HandleInstallationStoppage", "Cancelled by test", null!);
    }

    private void SetField(string name, object value) => typeof(InstallManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_fixture.Installer, value);

    private void CompleteUninstall(InstallItem install, Game game) => typeof(InstallManager)
        .GetMethod("CompleteUninstall", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_fixture.Installer, [install, game, game.LocalAppState!]);

    private static string Write(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic data");
        return path;
    }

    public void Dispose() => _fixture.Dispose();
}
