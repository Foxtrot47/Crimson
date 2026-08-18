using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class SyntheticUpdateLifecycleTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "SyntheticGame");

    [Fact]
    public async Task InstallRestartAndUpdate_PublishesExactNewVersionAndPreservesUserFiles()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(sandbox, "state");
        var installRoot = Path.Combine(sandbox, "game");
        Directory.CreateDirectory(installRoot);
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var versionOne = CreateHarness(logger, stateRoot, "old.manifest");
            versionOne.Storage.SaveMetaData(CreateGame("1.0.0"));

            var installResult = await RunOperationAsync(
                versionOne.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));
            Assert.Equal(ActionStatus.Success, installResult.Status);
            await AssertInstalledFilesAsync("old", installRoot);

            var unchangedPath = Path.Combine(installRoot, "Data", "unchanged.txt");
            var unchangedWriteTime = File.GetLastWriteTimeUtc(unchangedPath);
            var userFile = Path.Combine(installRoot, "Data", "user-save.dat");
            await File.WriteAllTextAsync(userFile, "preserve me");

            SimulateInterruptedPublication(versionOne.Storage, installRoot);
            var versionTwo = CreateHarness(logger, stateRoot, "new.manifest");
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.False(File.Exists(UpdateTransactionState.GetJournalPath(installRoot)));
            var updatedGame = versionTwo.Library.GetGameInfo("CrimsonSyntheticGame");
            Assert.NotNull(updatedGame);
            updatedGame.AssetInfos.Windows.BuildVersion = "2.0.0";
            versionTwo.Storage.SaveMetaData(updatedGame);

            versionTwo.Manager.UpdatePublicationFaultInjector = relativePath =>
            {
                if (relativePath == "Data/added.txt")
                    throw new IOException("Injected publication failure.");
            };
            var failedUpdate = await RunOperationAsync(
                versionTwo.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot));
            Assert.Equal(ActionStatus.Failed, failedUpdate.Status);
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.True(File.Exists(Path.Combine(installRoot, "Data", "removed.txt")));
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));

            var retry = CreateHarness(logger, stateRoot, "new.manifest");
            var journalTransitions = new List<JournalObservation>();
            retry.Manager.UpdateJournalTransitionFaultInjector = transaction =>
                journalTransitions.Add(new JournalObservation(
                    transaction.Revision,
                    transaction.Phase,
                    transaction.BackedUpPaths.ToArray(),
                    transaction.PublishedPaths.ToArray(),
                    File.Exists(UpdateTransactionState.GetJournalPath(transaction.InstallRoot)),
                    Directory.Exists(transaction.StagingRoot),
                    Directory.Exists(transaction.BackupRoot)));
            var updateResult = await RunOperationAsync(
                retry.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot));
            Assert.Equal(ActionStatus.Success, updateResult.Status);
            await AssertInstalledFilesAsync("new", installRoot);
            Assert.False(File.Exists(Path.Combine(installRoot, "Data", "removed.txt")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Data", "added.txt")));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));
            Assert.Equal(unchangedWriteTime, File.GetLastWriteTimeUtc(unchangedPath));
            Assert.Equal(Enumerable.Range(1, 8).Select(value => (long)value),
                journalTransitions.Select(transition => transition.Revision));
            Assert.Equal(UpdateTransactionPhase.Prepared, journalTransitions[0].Phase);
            Assert.Equal(UpdateTransactionPhase.MetadataCommitted, journalTransitions[^1].Phase);
            Assert.Equal(2, journalTransitions[^1].BackedUpPaths.Count);
            Assert.True(journalTransitions[^1].JournalExists);
            Assert.True(journalTransitions[^1].StagingExists);
            Assert.True(journalTransitions[^1].BackupExists);
            Assert.Equal(2, journalTransitions[^1].PublishedPaths.Count);
            Assert.False(File.Exists(UpdateTransactionState.GetJournalPath(installRoot)));

            var persisted = new Storage(logger, stateRoot);
            var installation = persisted.LocalAppStateDictionary["CrimsonSyntheticGame"];
            Assert.Equal(InstallState.Installed, installation.InstallStatus);
            Assert.Equal("2.0.0", installation.Version);
            Assert.Equal("2.0.0", installation.CachedManifestVersion);
            Assert.Equal("2.0.0", installation.InstalledManifestBuildVersion);
            Assert.Equal(
                Convert.ToHexString(SHA1.HashData(await File.ReadAllBytesAsync(
                    Path.Combine(FixtureRoot, "new.manifest")))).ToLowerInvariant(),
                installation.InstalledManifestSha1);
            Assert.Equal(
                "d81d6a12b85b9909a924de02f360f2da749cc1576d6320e015fc6f9dc9f58ebf",
                installation.InstalledManifestSha256);
            Assert.Equal(installation.InstalledManifestSha256, installation.AvailableManifestDigest);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task UpdateJournalFaultBeforeMetadataCommit_RestoresOldVersion(long faultRevision)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(sandbox, "state");
        var installRoot = Path.Combine(sandbox, "game");
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(logger, stateRoot, "old.manifest");
            harness.Storage.SaveMetaData(CreateGame("1.0.0"));
            Assert.Equal(ActionStatus.Success, (await RunOperationAsync(
                harness.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot))).Status);
            var userFile = Path.Combine(installRoot, "Data", "user-save.dat");
            await File.WriteAllTextAsync(userFile, "preserve me");

            var updater = CreateHarness(logger, stateRoot, "new.manifest");
            var game = updater.Library.GetGameInfo("CrimsonSyntheticGame");
            game.AssetInfos.Windows.BuildVersion = "2.0.0";
            updater.Storage.SaveMetaData(game);
            updater.Manager.UpdateJournalTransitionFaultInjector = transaction =>
            {
                if (transaction.Revision == faultRevision)
                    throw new IOException($"Injected journal fault at revision {faultRevision}.");
            };

            var result = await RunOperationAsync(
                updater.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot));

            Assert.Equal(ActionStatus.Failed, result.Status);
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));
            Assert.Equal(
                "1.0.0",
                updater.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"].Version);
            Assert.False(File.Exists(UpdateTransactionState.GetJournalPath(installRoot)));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataCommittedJournal_ReconcilesNewInstallationState()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(sandbox, "state");
        var installRoot = Path.Combine(sandbox, "game");
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var versionOne = CreateHarness(logger, stateRoot, "old.manifest");
            versionOne.Storage.SaveMetaData(CreateGame("1.0.0"));
            Assert.Equal(ActionStatus.Success, (await RunOperationAsync(
                versionOne.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot))).Status);
            var oldStateJson = JsonSerializer.Serialize(
                versionOne.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"]);

            var versionTwo = CreateHarness(logger, stateRoot, "new.manifest");
            var game = versionTwo.Library.GetGameInfo("CrimsonSyntheticGame");
            game.AssetInfos.Windows.BuildVersion = "2.0.0";
            versionTwo.Storage.SaveMetaData(game);
            Assert.Equal(ActionStatus.Success, (await RunOperationAsync(
                versionTwo.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Update, installRoot))).Status);
            var newState = versionTwo.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"];
            var newStateJson = JsonSerializer.Serialize(newState);
            var oldState = JsonSerializer.Deserialize<LocalAppState>(oldStateJson)!;
            versionTwo.Storage.AddToLocalAppState(oldState.AppName, oldState);

            var transaction = UpdateTransactionState.Create(
                installRoot,
                ["Data/changed.txt"],
                ["Data/added.txt"],
                ["Data/removed.txt"],
                [],
                oldStateJson,
                newStateJson);
            transaction.Phase = UpdateTransactionPhase.MetadataCommitted;
            transaction.Revision = 8;
            Directory.CreateDirectory(Path.GetDirectoryName(
                UpdateTransactionState.GetJournalPath(installRoot))!);
            AtomicFile.WriteAllBytes(
                UpdateTransactionState.GetJournalPath(installRoot),
                JsonSerializer.SerializeToUtf8Bytes(transaction));

            var recovered = CreateHarness(logger, stateRoot, "new.manifest");

            Assert.Equal(
                "2.0.0",
                recovered.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"].Version);
            await AssertInstalledFilesAsync("new", installRoot);
            Assert.False(File.Exists(UpdateTransactionState.GetJournalPath(installRoot)));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task RepairThenUninstall_RestoresTrackedFilesAndPreservesUserFiles()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(sandbox, "state");
        var installRoot = Path.Combine(sandbox, "game");
        Directory.CreateDirectory(installRoot);
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(logger, stateRoot, "old.manifest");
            harness.Storage.SaveMetaData(CreateGame("1.0.0"));
            var installResult = await RunOperationAsync(
                harness.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));
            Assert.Equal(ActionStatus.Success, installResult.Status);

            var userFile = Path.Combine(installRoot, "Data", "user-save.dat");
            await File.WriteAllTextAsync(userFile, "preserve me");
            await File.WriteAllTextAsync(Path.Combine(installRoot, "Data", "changed.txt"), "corrupt");

            var repairStatuses = new List<ActionStatus>();
            var repairResult = await RunOperationAsync(
                harness.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Repair, installRoot),
                repairStatuses);

            Assert.Equal(ActionStatus.Success, repairResult.Status);
            Assert.Equal([ActionStatus.Processing, ActionStatus.Success], repairStatuses);
            await AssertInstalledFilesAsync("old", installRoot);
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));

            var uninstallStatuses = new List<ActionStatus>();
            var uninstallResult = await RunOperationAsync(
                harness.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Uninstall, installRoot),
                uninstallStatuses);

            Assert.Equal(ActionStatus.Success, uninstallResult.Status);
            Assert.Equal([ActionStatus.Processing, ActionStatus.Success], uninstallStatuses);
            await AssertTrackedFilesDoNotExistAsync("old", installRoot);
            Assert.Equal("preserve me", await File.ReadAllTextAsync(userFile));
            var state = harness.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"];
            Assert.Equal(InstallState.NotInstalled, state.InstallStatus);
            Assert.Null(state.InstallPath);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task ImportThenMove_PreservesFilesAndUpdatesInstallLocation()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(sandbox, "source");
        var movedRoot = Path.Combine(sandbox, "moved");
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var materializer = CreateHarness(logger, Path.Combine(sandbox, "materializer-state"), "old.manifest");
            materializer.Storage.SaveMetaData(CreateGame("1.0.0"));
            var installResult = await RunOperationAsync(
                materializer.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, sourceRoot));
            Assert.Equal(ActionStatus.Success, installResult.Status);
            var userFile = Path.Combine(sourceRoot, "Data", "user-save.dat");
            await File.WriteAllTextAsync(userFile, "preserve me");

            var importer = CreateHarness(logger, Path.Combine(sandbox, "importer-state"), "old.manifest");
            importer.Storage.SaveMetaData(CreateGame("1.0.0"));
            var importStatuses = new List<ActionStatus>();
            var importResult = await RunOperationAsync(
                importer.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Import, sourceRoot),
                importStatuses);

            Assert.Equal(ActionStatus.Success, importResult.Status);
            Assert.Equal([ActionStatus.Processing, ActionStatus.Success], importStatuses);
            Assert.Equal(InstallState.Installed,
                importer.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"].InstallStatus);

            var moveStatuses = new List<ActionStatus>();
            var move = new InstallItem("CrimsonSyntheticGame", ActionType.Move, sourceRoot)
            {
                MoveLocation = movedRoot
            };
            var moveResult = await RunOperationAsync(importer.Manager, move, moveStatuses);

            Assert.Equal(ActionStatus.Success, moveResult.Status);
            Assert.Equal([ActionStatus.Processing, ActionStatus.Success], moveStatuses);
            Assert.False(Directory.Exists(sourceRoot));
            await AssertInstalledFilesAsync("old", movedRoot);
            Assert.Equal("preserve me", await File.ReadAllTextAsync(Path.Combine(movedRoot, "Data", "user-save.dat")));
            Assert.Equal(movedRoot,
                importer.Storage.LocalAppStateDictionary["CrimsonSyntheticGame"].InstallPath);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_PublishesTerminalOrderAndSalvagesCompletedFiles()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(sandbox, "game");
        var content = new BlockingContentHandler();
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(
                logger,
                Path.Combine(sandbox, "state"),
                "old.manifest",
                content);
            harness.Storage.SaveMetaData(CreateGame("1.0.0"));
            var statuses = new List<ActionStatus>();
            var terminal = new TaskCompletionSource<InstallItem>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Manager.InstallationStatusChanged += item =>
            {
                if (item.AppName != "CrimsonSyntheticGame")
                    return;
                statuses.Add(item.Status);
                if (item.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                    terminal.TrySetResult(item);
            };

            harness.Manager.AddToQueue(
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));
            await content.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Manager.StopProcessing();
            var result = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ActionStatus.Cancelled, result.Status);
            Assert.Equal(
                [ActionStatus.Processing, ActionStatus.Cancelling, ActionStatus.Cancelling,
                    ActionStatus.Cancelled],
                statuses);
            await AssertOnlyEmptyTrackedFilesExistAsync("old", installRoot);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task PauseThenResume_CompletesInstallAndPublishesCurrentOrder()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(sandbox, "game");
        var content = new PausableContentHandler();
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(
                logger,
                Path.Combine(sandbox, "state"),
                "old.manifest",
                content);
            harness.Storage.SaveMetaData(CreateGame("1.0.0"));
            var statuses = new List<ActionStatus>();
            var terminal = new TaskCompletionSource<InstallItem>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Manager.InstallationStatusChanged += item =>
            {
                if (item.AppName != "CrimsonSyntheticGame")
                    return;
                statuses.Add(item.Status);
                if (item.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                    terminal.TrySetResult(item);
            };

            harness.Manager.AddToQueue(
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));
            await content.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(harness.Manager.PauseInstall);
            Assert.Equal(ActionStatus.Paused, harness.Manager.CurrentInstall?.Status);

            var resume = Task.Run(harness.Manager.ResumeInstall);
            await WaitUntilAsync(
                () => statuses.Count(status => status == ActionStatus.Processing) >= 2,
                TimeSpan.FromSeconds(5));
            content.Release();
            await resume;
            var result = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => harness.Manager.CurrentInstall is null, TimeSpan.FromSeconds(5));

            Assert.Equal(ActionStatus.Success, result.Status);
            Assert.Equal(
                [ActionStatus.Processing, ActionStatus.Paused, ActionStatus.Processing,
                    ActionStatus.Success],
                statuses);
            await AssertInstalledFilesAsync("old", installRoot);
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Theory]
    [InlineData(InstallFileSystemLocation.Network, long.MaxValue)]
    [InlineData(InstallFileSystemLocation.Local, 0L)]
    public async Task Install_RejectsUnsupportedFilesystemOrCapacity(
        InstallFileSystemLocation location,
        long availableBytes)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(sandbox, "game");
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(
                logger,
                Path.Combine(sandbox, "state"),
                "old.manifest",
                fileSystemProbe: new StubFileSystemProbe(location, availableBytes));
            harness.Storage.SaveMetaData(CreateGame("1.0.0"));

            var result = await RunOperationAsync(
                harness.Manager,
                new InstallItem("CrimsonSyntheticGame", ActionType.Install, installRoot));

            Assert.Equal(ActionStatus.Failed, result.Status);
            Assert.False(File.Exists(Path.Combine(installRoot, "LaunchStub.cmd")));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task Move_RejectsDifferentVolumeIdentity()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-lifecycle-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(sandbox, "source");
        var destinationParent = Path.Combine(sandbox, "destination-volume");
        var destinationRoot = Path.Combine(destinationParent, "game");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationParent);
        using var logger = new LoggerConfiguration().CreateLogger();
        try
        {
            var harness = CreateHarness(
                logger,
                Path.Combine(sandbox, "state"),
                "old.manifest",
                fileSystemProbe: new PathIdentityFileSystemProbe(destinationParent));
            var game = CreateGame("1.0.0");
            game.LocalAppState = new LocalAppState
            {
                AppName = game.AppName,
                InstallPath = sourceRoot,
                InstallStatus = InstallState.Installed,
                Version = "1.0.0"
            };
            harness.Storage.SaveMetaData(game);
            harness.Storage.AddToLocalAppState(game.AppName, game.LocalAppState);
            var move = new InstallItem(game.AppName, ActionType.Move, sourceRoot)
            {
                MoveLocation = destinationRoot
            };

            var result = await RunOperationAsync(harness.Manager, move);

            Assert.Equal(ActionStatus.Failed, result.Status);
            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(destinationRoot));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    private static Harness CreateHarness(
        ILogger logger,
        string stateRoot,
        string manifestName,
        HttpMessageHandler? contentHandler = null,
        IInstallFileSystemProbe? fileSystemProbe = null)
    {
        var storage = new Storage(logger, stateRoot);
        var repository = new SyntheticRepository(manifestName);
        var auth = new AuthManager(
            logger,
            storage,
            new TestCredentialProtector(),
            new HttpClient(new RejectingHandler()));
        var library = new LibraryManager(
            logger,
            repository,
            storage,
            auth,
            new RecordingGameProcessRunner(),
            new LibraryService(repository, storage),
            new EpicLaunchPlanner(),
            new TestRuntimeProfileResolver(),
            new TestInstallRecoveryStatus());
        var contentClient = new HttpClient(contentHandler ?? new FixtureContentHandler());
        var downloads = new DownloadManager(NullLogger<DownloadManager>.Instance, contentClient);
        var manager = new InstallManager(
            logger,
            library,
            repository,
            storage,
            downloads,
            fileSystemProbe ?? new InstallFileSystemProbe());
        return new Harness(storage, library, manager);
    }

    private static Game CreateGame(string buildVersion) => new()
    {
        AppName = "CrimsonSyntheticGame",
        AppTitle = "Crimson Synthetic Game",
        AssetInfos = new AssetInfos
        {
            Windows = new Asset
            {
                AppName = "CrimsonSyntheticGame",
                BuildVersion = buildVersion,
                CatalogItemId = "synthetic-catalog",
                Namespace = "synthetic"
            }
        },
        Metadata = new Metadata
        {
            Id = "synthetic-catalog",
            Title = "Crimson Synthetic Game",
            Namespace = "synthetic"
        }
    };

    private static async Task<InstallItem> RunOperationAsync(
        InstallManager manager,
        InstallItem operation,
        ICollection<ActionStatus>? observedStatuses = null)
    {
        var completion = new TaskCompletionSource<InstallItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatusChanged(InstallItem item)
        {
            if (item.AppName != operation.AppName)
                return;
            observedStatuses?.Add(item.Status);
            if (item.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                completion.TrySetResult(item);
        }

        manager.InstallationStatusChanged += OnStatusChanged;
        try
        {
            manager.AddToQueue(operation);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => manager.CurrentInstall is null, TimeSpan.FromSeconds(5));
            return result;
        }
        finally
        {
            manager.InstallationStatusChanged -= OnStatusChanged;
        }
    }

    private static void SimulateInterruptedPublication(Storage storage, string installRoot)
    {
        var oldState = storage.LocalAppStateDictionary["CrimsonSyntheticGame"];
        var transaction = UpdateTransactionState.Create(
            installRoot,
            ["Data/changed.txt"],
            ["Data/added.txt"],
            ["Data/removed.txt"],
            [],
            JsonSerializer.Serialize(oldState),
            JsonSerializer.Serialize(oldState));
        transaction.Phase = UpdateTransactionPhase.Published;
        transaction.Revision = 3;
        transaction.BackedUpPaths.AddRange(transaction.ChangedPaths.Concat(transaction.RemovedPaths));
        transaction.PublishedPaths.AddRange(transaction.ChangedPaths.Concat(transaction.AddedPaths));
        Directory.CreateDirectory(transaction.StagingRoot);
        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.RemovedPaths))
        {
            var livePath = Path.Combine(installRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var backupPath = Path.Combine(
                transaction.BackupRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Move(livePath, backupPath);
        }

        var changedPath = Path.Combine(installRoot, "Data", "changed.txt");
        File.WriteAllText(changedPath, "partially published");
        File.WriteAllText(Path.Combine(installRoot, "Data", "added.txt"), "partially added");
        var journalPath = UpdateTransactionState.GetJournalPath(installRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(transaction));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Install manager did not reach its terminal state.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertInstalledFilesAsync(string version, string installRoot)
    {
        using var expected = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(FixtureRoot, "expected.json")));
        foreach (var property in expected.RootElement.GetProperty(version).GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(
                installRoot,
                property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing installed file: {property.Name}");
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(property.Value.GetProperty("size").GetInt64(), bytes.LongLength);
            Assert.Equal(
                property.Value.GetProperty("sha1").GetString(),
                Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
        }
    }

    private static async Task AssertTrackedFilesDoNotExistAsync(string version, string installRoot)
    {
        using var expected = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(FixtureRoot, "expected.json")));
        foreach (var property in expected.RootElement.GetProperty(version).GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(
                installRoot,
                property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(path), $"Tracked file was not uninstalled: {property.Name}");
        }
    }

    private static async Task AssertOnlyEmptyTrackedFilesExistAsync(string version, string installRoot)
    {
        using var expected = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(FixtureRoot, "expected.json")));
        foreach (var property in expected.RootElement.GetProperty(version).GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(
                installRoot,
                property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(property.Value.GetProperty("size").GetInt64() == 0, File.Exists(path));
        }
    }

    private sealed record Harness(Storage Storage, LibraryManager Library, InstallManager Manager);

    private sealed record JournalObservation(
        long Revision,
        UpdateTransactionPhase Phase,
        IReadOnlyList<string> BackedUpPaths,
        IReadOnlyList<string> PublishedPaths,
        bool JournalExists,
        bool StagingExists,
        bool BackupExists);

    private sealed class SyntheticRepository(string manifestName) : IStoreRepository
    {
        public Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(EpicPayloadPlatform.Windows, platform);
            var manifest = File.ReadAllBytes(Path.Combine(FixtureRoot, manifestName));
            return Task.FromResult(RepositoryResult<GetManifestUrlData>.Success(new GetManifestUrlData
            {
                BaseUrls = ["https://download.epicgames.com/synthetic"],
                ManifestUrls = ["https://download.epicgames.com/synthetic/" + manifestName],
                ManifestHash = Convert.ToHexString(SHA256.HashData(manifest))
            }));
        }

        public Task<RepositoryResult<byte[]>> GetGameManifest(
            GetManifestUrlData urlData,
            CancellationToken cancellationToken = default) => Task.FromResult(
            RepositoryResult<byte[]>.Success(
                File.ReadAllBytes(Path.Combine(FixtureRoot, manifestName))));

        public Task<RepositoryResult<Metadata>> FetchGameMetaData(
            string nameSpace,
            string catalogItemId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<long>> DownloadFileAsync(
            string url,
            string destinationPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<string>> GetGameToken(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixtureContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateFixtureResponse(request));
    }

    private sealed class PausableContentHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateFixtureResponse(request);
        }
    }

    private static HttpResponseMessage CreateFixtureResponse(HttpRequestMessage request)
    {
        const string prefix = "/synthetic/";
        var path = request.RequestUri?.AbsolutePath
            ?? throw new InvalidOperationException("Synthetic request URI is missing.");
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var relative = Uri.UnescapeDataString(path[prefix.Length..])
            .Replace('/', Path.DirectorySeparatorChar);
        var file = Path.GetFullPath(Path.Combine(FixtureRoot, relative));
        if (!file.StartsWith(Path.GetFullPath(FixtureRoot), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(file))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(file))
        };
    }

    private sealed class PathIdentityFileSystemProbe(string destinationParent)
        : IInstallFileSystemProbe
    {
        public InstallFileSystemProbeResult Probe(string directoryPath)
        {
            var destination = Path.GetFullPath(destinationParent);
            var candidate = Path.GetFullPath(directoryPath);
            var isDestination = candidate.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new InstallFileSystemProbeResult(
                true,
                VolumeIdentity: isDestination ? "destination-volume" : "source-volume",
                AvailableBytes: long.MaxValue,
                TotalBytes: long.MaxValue,
                AtomicRenameSupported: true,
                Location: InstallFileSystemLocation.Local);
        }
    }

    private sealed class StubFileSystemProbe(
        InstallFileSystemLocation location,
        long availableBytes) : IInstallFileSystemProbe
    {
        public InstallFileSystemProbeResult Probe(string directoryPath) => new(
            true,
            VolumeIdentity: "test-volume",
            AvailableBytes: availableBytes,
            TotalBytes: long.MaxValue,
            AtomicRenameSupported: true,
            Location: location);
    }

    private sealed class BlockingContentHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Blocked synthetic request completed unexpectedly.");
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Authentication HTTP client is not used by the synthetic lifecycle.");
    }
}
