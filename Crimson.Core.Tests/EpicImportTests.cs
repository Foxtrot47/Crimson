using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class EpicImportTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    public EpicImportTests()
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        _fixture.BaseGame.LocalAppState.Version = null!;
        _fixture.Storage.SaveUserData(new UserData { AccountId = "synthetic-import-owner" }).GetAwaiter().GetResult();
        typeof(LibraryManager).GetField("_lastUpdateDateTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Library, DateTime.Now);
    }

    [Fact]
    public async Task DiscoversInstalledGameWithoutChangingEpicFiles()
    {
        var source = await CreateInstallation();
        var original = await File.ReadAllBytesAsync(RecordPath(source));
        var manifest = await File.ReadAllBytesAsync(ManifestPath(source));

        var scan = await _fixture.EpicDiscovery.DiscoverAsync();

        var found = Assert.Single(scan.Installations);
        Assert.Equal(source.AppName, found.AppName);
        Assert.Equal(source.InstallLocation, found.InstallLocation);
        Assert.Equal(0, scan.UnreadableCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(RecordPath(source)));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(ManifestPath(source)));
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task Utf8ByteOrderMarkIsAccepted()
    {
        var source = await CreateInstallation();
        var bytes = await File.ReadAllBytesAsync(RecordPath(source));
        await File.WriteAllBytesAsync(RecordPath(source), [0xEF, 0xBB, 0xBF, .. bytes]);
        Assert.Single((await _fixture.EpicDiscovery.DiscoverAsync()).Installations);
    }

    [Fact]
    public async Task MissingEpicLauncherReturnsAnEmptyScan()
    {
        Assert.Empty((await _fixture.EpicDiscovery.DiscoverAsync()).Installations);
    }

    [Theory]
    [InlineData("AppName", "")]
    [InlineData("InstallLocation", "relative-folder")]
    [InlineData("InstallationGuid", "../outside")]
    [InlineData("CatalogNamespace", "")]
    public async Task InvalidRecordsAreCountedWithoutSuppressingValidGames(string field, string value)
    {
        var source = await CreateInstallation();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath(source)))!;
        json[field] = value;
        await File.WriteAllTextAsync(Path.Combine(_fixture.EpicRecordsPath, "invalid.item"), json.ToJsonString());

        var scan = await _fixture.EpicDiscovery.DiscoverAsync();

        Assert.Single(scan.Installations);
        Assert.Equal(1, scan.UnreadableCount);
    }

    [Fact]
    public async Task IncompleteAndMissingInstallationsAreNotOffered()
    {
        var source = await CreateInstallation();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath(source)))!;
        json["bIsIncompleteInstall"] = true;
        await File.WriteAllTextAsync(RecordPath(source), json.ToJsonString());
        Assert.Empty((await _fixture.EpicDiscovery.DiscoverAsync()).Installations);
        json["bIsIncompleteInstall"] = false;
        await File.WriteAllTextAsync(RecordPath(source), json.ToJsonString());
        File.Delete(ManifestPath(source));
        var scan = await _fixture.EpicDiscovery.DiscoverAsync();
        Assert.Empty(scan.Installations);
        Assert.Equal(1, scan.UnreadableCount);
    }

    [Fact]
    public async Task DuplicateRecordsAreDeduplicatedAndConflictingLocationsFailClosed()
    {
        var source = await CreateInstallation();
        var json = await File.ReadAllTextAsync(RecordPath(source));
        var duplicate = Path.Combine(_fixture.EpicRecordsPath, "duplicate.item");
        await File.WriteAllTextAsync(duplicate, json);
        Assert.Single((await _fixture.EpicDiscovery.DiscoverAsync()).Installations);
        var conflict = JsonNode.Parse(json)!;
        var other = Path.Combine(_fixture.Root, "other-game");
        Directory.CreateDirectory(Path.Combine(other, ".egstore"));
        await File.WriteAllBytesAsync(Path.Combine(other, ".egstore", source.InstallationGuid + ".manifest"), [1]);
        conflict["InstallLocation"] = other;
        await File.WriteAllTextAsync(duplicate, conflict.ToJsonString());
        var scan = await _fixture.EpicDiscovery.DiscoverAsync();
        Assert.Empty(scan.Installations);
        Assert.Equal(1, scan.UnreadableCount);
    }

    [Fact]
    public async Task MalformedAndOversizedRecordsDoNotBreakThePicker()
    {
        await CreateInstallation();
        await File.WriteAllTextAsync(Path.Combine(_fixture.EpicRecordsPath, "malformed.item"), "{broken");
        await File.WriteAllBytesAsync(Path.Combine(_fixture.EpicRecordsPath, "oversized.item"), new byte[1024 * 1024 + 1]);
        var scan = await _fixture.EpicDiscovery.DiscoverAsync();
        Assert.Single(scan.Installations);
        Assert.Equal(2, scan.UnreadableCount);
    }

    [Fact]
    public async Task PickerShowsDetectedEntriesWithoutWarningsAboutSkippedRecords()
    {
        var source = await CreateInstallation();
        await File.WriteAllTextAsync(Path.Combine(_fixture.EpicRecordsPath, "unreadable.item"), "{broken");
        var model = ViewModel();

        await model.LoadAsync(CancellationToken.None);

        var choice = Assert.Single(model.Items);
        Assert.Equal(source.InstallLocation, choice.Installation.InstallLocation);
        Assert.Equal(source.BuildVersion, choice.Installation.BuildVersion);
        Assert.Empty(model.StatusMessage);
        Assert.False(model.HasStatusMessage);
        Assert.False(model.IsBusy);
        Assert.False(choice.IsSelected);
        Assert.Null(choice.ImageUrl);
    }

    [Theory]
    [InlineData("DieselGameBoxTall", "https://cdn1.epicgames.com/images/cover.png", true)]
    [InlineData("DieselGameBoxTall", "http://cdn1.epicgames.com/images/cover.png", false)]
    [InlineData("DieselGameBoxTall", "https://example.test/cover.png", false)]
    [InlineData("DieselGameBoxTall", "file:///cover.png", false)]
    [InlineData("OfferImageWide", "https://cdn1.epicgames.com/images/cover.png", false)]
    public async Task PickerUsesOnlyApprovedPortraitArtwork(string type, string url, bool expected)
    {
        await CreateInstallation();
        _fixture.BaseGame.Metadata.KeyImages = [new KeyImage { Type = type, Url = url }];
        var model = ViewModel();

        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(expected ? url : null, Assert.Single(model.Items).ImageUrl);
    }

    [Fact]
    public async Task PickerStartsUncheckedAndQueuesOnlySelectedGames()
    {
        await CreateInstallation();
        await CreateOtherGame();
        _fixture.SetCurrent("unrelated-operation");
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Equal(2, model.Items.Count);
        Assert.Equal("Another game", model.Items[0].Title);
        var choice = Assert.Single(model.Items, item => item.Installation.AppName == "base");
        Assert.False(choice.IsSelected);
        Assert.False(model.CanImport);
        Assert.Empty(_fixture.Queue());
        choice.IsSelected = true;

        Assert.True(await model.ImportSelectedAsync(CancellationToken.None));

        var queued = Assert.Single(_fixture.Queue());
        Assert.Equal(ActionType.Import, queued.Action);
        Assert.Equal(choice.Installation, queued.EpicImportSource);
        Assert.Equal(choice.Installation.InstallLocation, queued.Location);
    }

    [Fact]
    public async Task MultipleSelectedGamesAreQueuedIndividually()
    {
        await CreateInstallation();
        await CreateOtherGame();
        _fixture.SetCurrent("unrelated-operation");
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        foreach (var item in model.Items) item.IsSelected = true;
        Assert.True(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Equal(2, _fixture.Queue().Count);
        Assert.Equal(2, _fixture.Queue().Select(item => item.OperationId).Distinct().Count());
        Assert.All(_fixture.Queue(), item => Assert.Equal(ActionType.Import, item.Action));
    }

    [Fact]
    public async Task TamperedRecoverySourceCannotReadOutsideEpicRecords()
    {
        var source = await CreateInstallation();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _fixture.EpicDiscovery.ReadManifestAsync(source with { RecordName = "../outside.item" }, CancellationToken.None));
        Assert.Null(await _fixture.Storage.GetCachedManifestBytes(source.AppName, source.BuildVersion));
    }

    [Theory]
    [InlineData(InstallState.Installed)]
    [InlineData(InstallState.NeedUpdate)]
    [InlineData(InstallState.Broken)]
    [InlineData(InstallState.Playing)]
    [InlineData(InstallState.InstallationPaused)]
    public async Task AlreadyTrackedGamesAreNotOffered(InstallState state)
    {
        await CreateInstallation();
        _fixture.BaseGame.LocalAppState!.InstallStatus = state;
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task ActiveRelatedOperationBlocksImport()
    {
        await CreateInstallation();
        _fixture.SetCurrent("installed");
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Empty(model.Items);
    }

    [Theory]
    [InlineData("CatalogNamespace")]
    [InlineData("CatalogItemId")]
    [InlineData("AppName")]
    public async Task MatchesEpicIdentifiersNotDisplayNames(string field)
    {
        var source = await CreateInstallation();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath(source)))!;
        json[field] = "unrelated";
        json["DisplayName"] = _fixture.BaseGame.AppTitle;
        await File.WriteAllTextAsync(RecordPath(source), json.ToJsonString());
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task UnownedGamesAndDlcAreNotOffered()
    {
        await CreateInstallation();
        await CreateInstallation(_fixture.InstalledDlc);
        _fixture.SetOwned("installed");
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task OwnershipIsRecheckedAfterSelection()
    {
        await CreateInstallation();
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        _fixture.SetOwned();
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Empty(_fixture.Queue());
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task AccountChangeCannotImportEvenIfTheNewAccountOwnsTheSameGame()
    {
        await CreateInstallation();
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        await _fixture.Storage.ClearUserData();
        _fixture.Storage.SaveMetaData(_fixture.BaseGame);
        _fixture.SetOwned("base");
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Empty(_fixture.Queue());
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task QueuedImportCannotExecuteAfterAnAccountChangeEvenWhenBothAccountsOwnTheGame()
    {
        var source = await CreateInstallation();
        _fixture.SetCurrent("unrelated-operation");
        Assert.True(await _fixture.Installer.TryQueueEpicImportAsync(source, _fixture.Storage.AccountGeneration));
        var queued = Assert.Single(_fixture.Queue());
        await _fixture.Storage.ClearUserData();
        await _fixture.Storage.SaveUserData(new UserData { AccountId = "another-synthetic-owner" });
        _fixture.Storage.SaveMetaData(_fixture.BaseGame);
        _fixture.SetOwned("base");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _fixture.Invoke("GetEpicImportManifestBytesAsync", queued, CancellationToken.None));

        Assert.Null(await _fixture.Storage.GetCachedManifestBytes(source.AppName, source.BuildVersion));
        Assert.Equal(InstallState.NotInstalled, _fixture.BaseGame.LocalAppState!.InstallStatus);
    }

    [Fact]
    public async Task AccountChangeAfterVerificationCannotCommitTheImport()
    {
        var source = await CreateInstallation();
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "synthetic game");
        var item = await BoundImport(source);
        var manifest = Manifest.ReadAll(await File.ReadAllBytesAsync(ManifestPath(source)));
        await _fixture.Invoke("PrepareImportTasks", item, manifest);
        await _fixture.Storage.ClearUserData();
        await _fixture.Storage.SaveUserData(new UserData { AccountId = "another-synthetic-owner" });
        _fixture.Storage.SaveMetaData(_fixture.BaseGame);
        _fixture.SetOwned("base");

        var error = Assert.Throws<TargetInvocationException>(() => typeof(InstallManager)
            .GetMethod("CompleteImportAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_fixture.Installer, [item, _fixture.BaseGame, _fixture.BaseGame.LocalAppState!]));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(InstallState.NotInstalled, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.Null(_fixture.BaseGame.LocalAppState.Version);
    }

    [Fact]
    public async Task RecoveryRejectsAnotherOwnerEvenWhenTheProcessGenerationNumberMatches()
    {
        var source = await CreateInstallation();
        var pending = await BoundImport(source);
        _fixture.Storage.SaveInstallState(JsonSerializer.Serialize(new { CurrentInstall = pending }));
        await _fixture.Storage.SaveUserData(new UserData { AccountId = "another-synthetic-owner" });

        await _fixture.Installer.LoadPendingInstalls();

        Assert.Null(_fixture.Installer.CurrentInstall);
        var history = Assert.Single(_fixture.Installer.GetHistoryOperations());
        Assert.Equal(ActionStatus.Failed, history.Status);
        Assert.Equal(InstallState.NotInstalled, _fixture.BaseGame.LocalAppState!.InstallStatus);
    }

    [Fact]
    public async Task RecoveryForTheSameOwnerRebindsOnlyTheNewProcessGeneration()
    {
        var source = await CreateInstallation(version: "older-build");
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "synthetic game");
        var generation = typeof(Crimson.Utils.Storage).GetProperty("AccountGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        generation.SetValue(_fixture.Storage, 5);
        _fixture.SetOwned("base");
        var pending = await BoundImport(source);
        _fixture.Storage.SaveInstallState(JsonSerializer.Serialize(new { CurrentInstall = pending }));
        generation.SetValue(_fixture.Storage, 0);
        _fixture.SetOwned("base");

        await _fixture.Installer.LoadPendingInstalls();

        var restored = Assert.IsType<InstallItem>(_fixture.Installer.CurrentInstall);
        Assert.Equal(ActionStatus.Paused, restored.Status);
        Assert.Equal(pending.EpicImportOwner, restored.EpicImportOwner);
        Assert.Equal(0, restored.EpicImportAccountGeneration);
        Assert.Equal("older-build", restored.EpicImportSource!.BuildVersion);
    }

    [Fact]
    public async Task ReservationBeforeSnapshotPublicationBlocksEpicImportAtomically()
    {
        var source = await CreateInstallation();
        var related = new InstallItem("installed", ActionType.Install, _fixture.BasePath);
        Assert.True(_fixture.Library.TryQueueOperation(OperationSnapshot.From(related), () => { }));
        Assert.False(await _fixture.Installer.TryQueueEpicImportAsync(source, _fixture.Storage.AccountGeneration));
        Assert.Empty(_fixture.Queue());
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task ReplacedInstallationStateWhilePickerWasOpenIsNotOverwritten()
    {
        await CreateInstallation();
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        _fixture.Storage.AddToLocalAppState("base", new LocalAppState { AppName = "base", InstallStatus = InstallState.Installed });
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Empty(_fixture.Queue());
    }

    [Fact]
    public async Task OperationStartedWhilePickerWasOpenIsNotOverwritten()
    {
        await CreateInstallation();
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        _fixture.SetCurrent("base");
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Empty(_fixture.Queue());
    }

    [Fact]
    public async Task ChangedEpicRecordRequiresDetectionAgain()
    {
        var source = await CreateInstallation();
        var model = ViewModel();
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RecordPath(source)))!;
        json["AppVersionString"] = "changed-build";
        await File.WriteAllTextAsync(RecordPath(source), json.ToJsonString());
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        var error = Assert.Single(errors);
        Assert.Equal("epic_install_import_failed", error.Failure?.Code);
        Assert.False(error.ShowErrorDialog);
        Assert.Contains("could not be queued", model.StatusMessage);
        Assert.Empty(_fixture.Queue());
    }

    [Fact]
    public async Task CancelOrCloseDoesNotQueueGames()
    {
        await CreateInstallation();
        var model = ViewModel();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await model.LoadAsync(cancelled.Token);
        Assert.Empty(model.Items);
        await model.LoadAsync(CancellationToken.None);
        Assert.Single(model.Items).IsSelected = true;
        model.Close();
        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));
        Assert.Empty(_fixture.Queue());
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Theory]
    [InlineData("latest-build", InstallState.Installed)]
    [InlineData("older-build", InstallState.NeedUpdate)]
    public async Task ImportVerifiesTheActualInstalledBuildWithoutNetworkAndCachesItsManifest(string build, InstallState expected)
    {
        var source = await CreateInstallation(version: build);
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "synthetic game");
        var record = await File.ReadAllBytesAsync(RecordPath(source));
        var manifest = await File.ReadAllBytesAsync(ManifestPath(source));
        var item = new InstallItem(source.AppName, ActionType.Import, source.InstallLocation) { EpicImportSource = source };

        await RunToTerminal(item);

        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(expected, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.Equal(build, _fixture.BaseGame.LocalAppState.Version);
        Assert.Equal(manifest, await _fixture.Storage.GetCachedManifestBytes(source.AppName, build));
        Assert.Equal(record, await File.ReadAllBytesAsync(RecordPath(source)));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(ManifestPath(source)));
        Assert.Equal("synthetic game", await File.ReadAllTextAsync(Path.Combine(source.InstallLocation, "game.exe")));
    }

    [Theory]
    [InlineData("AppNameString", "other-app")]
    [InlineData("BuildVersionString", "other-build")]
    public async Task MismatchedInstalledManifestCannotCertifyAnInstallation(string field, string value)
    {
        var source = await CreateInstallation();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath(source)))!;
        json[field] = value;
        await File.WriteAllTextAsync(ManifestPath(source), json.ToJsonString());
        var item = new InstallItem(source.AppName, ActionType.Import, source.InstallLocation) { EpicImportSource = source };
        await RunToTerminal(item);
        Assert.Equal(ActionStatus.Failed, item.Status);
        Assert.Equal(InstallState.NotInstalled, _fixture.BaseGame.LocalAppState!.InstallStatus);
    }

    [Fact]
    public async Task CorruptFilesRemainRepairableWithoutAutomaticReplacement()
    {
        var source = await CreateInstallation();
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "corrupt game");
        var item = new InstallItem(source.AppName, ActionType.Import, source.InstallLocation) { EpicImportSource = source };
        await RunToTerminal(item);
        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(InstallState.Broken, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.Null(_fixture.BaseGame.LocalAppState.Version);
        Assert.Equal("corrupt game", await File.ReadAllTextAsync(Path.Combine(source.InstallLocation, "game.exe")));
    }

    [Fact]
    public async Task SelectedInstallTagsDoNotRequireUnselectedOptionalFiles()
    {
        var source = await CreateInstallation(tags: ["english"]);
        var json = ImportFlowTests.CreateImportManifest(["game.exe", "english.bin", "french.bin"]);
        json["AppNameString"] = source.AppName;
        json["BuildVersionString"] = source.BuildVersion;
        json["FileManifestList"]![1]!["InstallTags"] = new JsonArray("english");
        json["FileManifestList"]![2]!["InstallTags"] = new JsonArray("french");
        await File.WriteAllTextAsync(ManifestPath(source), json.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "synthetic game");
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "english.bin"), "synthetic game");
        var item = new InstallItem(source.AppName, ActionType.Import, source.InstallLocation) { EpicImportSource = source };
        await RunToTerminal(item);
        Assert.Equal(ActionStatus.Success, item.Status);
        Assert.Equal(InstallState.Installed, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.Equal("Verified 2 files", item.StatusMessage);
        Assert.Equal(["english"], Assert.IsType<string[]>(_fixture.BaseGame.LocalAppState.InstallTags));
        var saved = JsonSerializer.Deserialize<LocalAppState>(JsonSerializer.Serialize(_fixture.BaseGame.LocalAppState))!;
        Assert.Equal(["english"], Assert.IsType<string[]>(saved.InstallTags));
        _fixture.SetCurrent("base");
        var verify = new InstallItem("base", ActionType.Verify, source.InstallLocation);
        await _fixture.Invoke("CompleteInstallAsync", verify, _fixture.BaseGame, _fixture.BaseGame.LocalAppState);
        Assert.Equal(InstallState.Installed, _fixture.BaseGame.LocalAppState.InstallStatus);
        var previous = await (Task<Manifest?>)typeof(InstallManager)
            .GetMethod("GetUpdateBaseManifestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_fixture.Installer, null)!;
        Assert.Equal(2, previous!.FileManifestList.Elements.Count);
    }

    [Fact]
    public async Task EpicSourceSurvivesPendingOperationSerialization()
    {
        var source = await CreateInstallation(tags: ["english"]);
        var item = await BoundImport(source);
        var restored = JsonSerializer.Deserialize<InstallItem>(JsonSerializer.Serialize(item))!;
        Assert.Equal(item.EpicImportOwner, restored.EpicImportOwner);
        Assert.Null(restored.EpicImportAccountGeneration);
        Assert.Equal(source.AppName, restored.EpicImportSource!.AppName);
        Assert.Equal(source.InstallationGuid, restored.EpicImportSource.InstallationGuid);
        Assert.Equal(source.InstallTags, restored.EpicImportSource.InstallTags);
        Assert.Equal(source.BuildVersion, restored.EpicImportSource.BuildVersion);
    }

    [Fact]
    public async Task RestoringAPendingEpicImportKeepsItsInstalledBuildAndComponentSelection()
    {
        var source = await CreateInstallation(version: "older-build", tags: []);
        await File.WriteAllTextAsync(Path.Combine(source.InstallLocation, "game.exe"), "synthetic game");
        var pending = await BoundImport(source);
        _fixture.Storage.SaveInstallState(JsonSerializer.Serialize(new { CurrentInstall = pending }));

        await _fixture.Installer.LoadPendingInstalls();

        var restored = Assert.IsType<InstallItem>(_fixture.Installer.CurrentInstall);
        Assert.Equal(ActionStatus.Paused, restored.Status);
        Assert.Equal("older-build", restored.EpicImportSource!.BuildVersion);
        Assert.Empty(restored.EpicImportSource.InstallTags!);
        Assert.Equal(source.InstallationGuid, restored.EpicImportSource.InstallationGuid);
        Assert.Equal(ActionType.Import, restored.Action);
        Assert.NotNull(await _fixture.Storage.GetCachedManifestBytes(source.AppName, "older-build"));
    }

    [Fact]
    public async Task RetryingAnEpicImportKeepsItsSourceInsteadOfFallingBackToTheLatestBuild()
    {
        var source = await CreateInstallation(version: "older-build");
        var failed = new InstallItem(source.AppName, ActionType.Import, source.InstallLocation)
        {
            EpicImportSource = source, EpicImportOwner = await _fixture.Installer.GetEpicImportOwnerAsync(),
            EpicImportAccountGeneration = _fixture.Storage.AccountGeneration, Status = ActionStatus.Failed,
            Failure = new OperationFailure(OperationFailureKind.Network, "synthetic_network_failure", "Synthetic failure", "Try again.", true)
        };
        var history = (List<InstallItem>)typeof(InstallManager)
            .GetField("_installHistory", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_fixture.Installer)!;
        history.Add(failed);
        _fixture.SetCurrent("unrelated-operation");
        Assert.True(_fixture.Installer.RetryFailedOperation(source.AppName));
        Assert.Equal(source, Assert.Single(_fixture.Queue()).EpicImportSource);
    }

    [Fact]
    public async Task LinkedEpicManifestDirectoriesAreRejected()
    {
        var source = await CreateInstallation();
        var original = Path.GetDirectoryName(ManifestPath(source))!;
        var outside = Path.Combine(_fixture.Root, "outside-manifests");
        Directory.Move(original, outside);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe", Arguments = $"/c mklink /J \"{original}\" \"{outside}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                Assert.NotNull(process);
                Assert.True(process.WaitForExit(5000));
                Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(original, outside);
            var scan = await _fixture.EpicDiscovery.DiscoverAsync();
            Assert.Empty(scan.Installations);
            Assert.Equal(1, scan.UnreadableCount);
            await Assert.ThrowsAsync<InvalidDataException>(() => _fixture.EpicDiscovery.ReadManifestAsync(source, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(original)) Directory.Delete(original);
        }
    }

    private Task<EpicInstallation> CreateOtherGame()
    {
        var game = _fixture.Library.GetGameInfo("available");
        game.Metadata.MainGameItem = null!;
        game.AppTitle = "Another game";
        return CreateInstallation(game);
    }

    private EpicImportViewModel ViewModel() =>
        new(_fixture.EpicDiscovery, _fixture.Installer, _fixture.Library, _fixture.Storage);

    private async Task<EpicInstallation> CreateInstallation(Game? game = null, string version = "latest-build", string[]? tags = null)
    {
        game ??= _fixture.BaseGame;
        Directory.CreateDirectory(_fixture.EpicRecordsPath);
        var guid = Guid.NewGuid().ToString("N");
        var source = new EpicInstallation(guid + ".item", game.AppName, game.AssetInfos.Windows.Namespace,
            game.AssetInfos.Windows.CatalogItemId, _fixture.BasePath, guid, version, tags);
        var record = new JsonObject
        {
            ["AppName"] = source.AppName, ["CatalogNamespace"] = source.CatalogNamespace,
            ["CatalogItemId"] = source.CatalogItemId, ["InstallLocation"] = source.InstallLocation,
            ["InstallationGuid"] = guid, ["AppVersionString"] = version, ["bIsIncompleteInstall"] = false
        };
        if (tags is not null) record["InstallTags"] = JsonSerializer.SerializeToNode(tags);
        await File.WriteAllTextAsync(RecordPath(source), record.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(source))!);
        var manifest = ImportFlowTests.CreateImportManifest(["game.exe"]);
        manifest["AppNameString"] = game.AppName;
        manifest["BuildVersionString"] = version;
        manifest["LaunchExeString"] = "game.exe";
        await File.WriteAllTextAsync(ManifestPath(source), manifest.ToJsonString());
        return source;
    }

    private string RecordPath(EpicInstallation source) => Path.Combine(_fixture.EpicRecordsPath, source.RecordName);
    private static string ManifestPath(EpicInstallation source) =>
        Path.Combine(source.InstallLocation, ".egstore", source.InstallationGuid + ".manifest");

    private async Task<InstallItem> BoundImport(EpicInstallation source) => new(source.AppName, ActionType.Import, source.InstallLocation)
    {
        EpicImportSource = source,
        EpicImportOwner = await _fixture.Installer.GetEpicImportOwnerAsync(),
        EpicImportAccountGeneration = _fixture.Storage.AccountGeneration
    };

    private async Task RunToTerminal(InstallItem item)
    {
        item.EpicImportOwner = await _fixture.Installer.GetEpicImportOwnerAsync();
        item.EpicImportAccountGeneration = _fixture.Storage.AccountGeneration;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Installer.InstallationStatusChanged += updated =>
        {
            if (ReferenceEquals(item, updated) && updated.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                completion.TrySetResult();
        };
        _fixture.Installer.AddToQueue(item);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (ReferenceEquals(_fixture.Installer.CurrentInstall, item)) await Task.Delay(1);
    }

    public void Dispose() => _fixture.Dispose();
}
