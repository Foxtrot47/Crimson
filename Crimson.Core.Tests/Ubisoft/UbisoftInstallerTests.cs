using Crimson.Resources;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using ZstdSharp;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Core.Tests.Ubisoft;

public sealed class UbisoftInstallerTests : IDisposable
{
    private const uint Product = 4242;
    private static readonly string App = new GameId(UbisoftLauncherProvider.ProviderId, "4242").ToString();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProducts _products = new();
    private readonly FakeSource _source = new();
    private readonly FakeCdn _cdn = new();
    private readonly FakeSetup _setup = new();
    private readonly FakeRegistry _registry = new();
    private readonly UbisoftInstalledStore _store;
    private readonly UbisoftInstallManager _manager;

    public UbisoftInstallerTests()
    {
        Directory.CreateDirectory(_folder);
        _store = new UbisoftInstalledStore(ProviderRoot);
        _manager = new UbisoftInstallManager(_products, _store, _setup,
            (_, _) => Task.FromResult<IUbisoftContentSource>(_source), _cdn, _registry, ProviderRoot, Runtime,
            Serilog.Core.Logger.None) { RetryDelay = TimeSpan.Zero };
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string ProviderRoot => Path.Combine(_folder, "provider");
    private string Runtime => Path.Combine(_folder, "Crimson", UbisoftSystemSetupPolicy.CrimsonRuntimeFolder);
    private string GameRoot => Path.Combine(_folder, "games", "Test Game");

    [Theory]
    [InlineData(UbisoftCompression.Zstd)]
    [InlineData(UbisoftCompression.Deflate)]
    public async Task Install_writes_compressed_and_stored_slices_at_their_offsets(UbisoftCompression compression)
    {
        var game = Content("game", 10_000);
        var noise = Noise(3_000, seed: 1);
        var build = new ManifestBuild(compression).Chunk(1)
            .File(@"bin\game.exe", game, sliceSize: 4096)
            // Odd slices are stored as-is (incompressible data is published uncompressed in a compressed build).
            .File(@"data\noise.big", noise, sliceSize: 1024, stored: index => index % 2 == 1);
        Publish(build);

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(game, File.ReadAllBytes(Path.Combine(GameRoot, "bin", "game.exe")));
        Assert.Equal(noise, File.ReadAllBytes(Path.Combine(GameRoot, "data", "noise.big")));
        var record = Assert.IsType<UbisoftInstalledGame>(_store.Find(Product));
        Assert.Equal(build.ManifestId, record.ManifestId);
        Assert.Equal((ulong)(game.Length + noise.Length), record.InstallBytes);
        Assert.False(record.SystemSetupPending);
        Assert.False(File.Exists(Path.Combine(GameRoot, ".Crimson", "ubisoft-progress.json")));
    }

    [Fact]
    public async Task Install_into_a_folder_with_foreign_content_is_refused_and_touches_nothing()
    {
        Publish(new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500)));
        Directory.CreateDirectory(GameRoot);
        var foreign = Path.Combine(GameRoot, "notes.txt");
        File.WriteAllText(foreign, "mine");

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Equal("folder_not_empty", result.Failure?.Code);
        Assert.Equal(new[] { "notes.txt" }, Directory.EnumerateFileSystemEntries(GameRoot).Select(Path.GetFileName));
        Assert.Equal("mine", File.ReadAllText(foreign));
        Assert.Empty(_setup.Requests);
    }

    [Fact]
    public async Task Update_replaces_a_file_that_became_a_directory_and_the_reverse()
    {
        var first = new ManifestBuild().Chunk(1).File("thing", Content("old", 100));
        Publish(first);
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);

        var second = new ManifestBuild().Chunk(1)
            .Directory("thing")
            .File("thing\\child.bin", Content("child", 200));
        Publish(second);
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Update, GameRoot))).Status);
        Assert.True(File.Exists(Path.Combine(GameRoot, "thing", "child.bin")));

        var third = new ManifestBuild().Chunk(1).File("thing", Content("replacement", 300));
        Publish(third);
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Update, GameRoot))).Status);
        Assert.Equal(Content("replacement", 300), await File.ReadAllBytesAsync(Path.Combine(GameRoot, "thing")));
    }

    [Fact]
    public void Runtime_copy_is_rolled_back_when_its_record_cannot_be_written()
    {
        var source = Path.Combine(_folder, "bundled");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "uplay_r1.dll"), "runtime");
        Directory.CreateDirectory(GameRoot);
        File.WriteAllText(Path.Combine(GameRoot, ".Crimson"), "blocks the record folder");

        var copied = UbisoftRuntimeFiles.Provision(GameRoot, [GameRoot], source);

        Assert.Empty(copied);
        Assert.False(File.Exists(Path.Combine(GameRoot, "uplay_r1.dll")));
    }

    [Fact]
    public async Task Install_rejects_a_manifest_that_does_not_match_its_published_id()
    {
        var build = new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500));
        Publish(build);
        var path = $"manifests/{build.ManifestId}.manifest";
        _cdn.Objects[path][^1] ^= 0xFF;

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Null(_store.Find(Product));
    }

    [Fact]
    public async Task A_tampered_cached_manifest_is_discarded_and_refetched()
    {
        var build = new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500));
        Publish(build);
        var cache = Path.Combine(ProviderRoot, "manifests", build.ManifestId + ".manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache, "not the signed manifest");

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(_cdn.Objects[$"manifests/{build.ManifestId}.manifest"], await File.ReadAllBytesAsync(cache));
    }

    [Fact]
    public async Task A_completed_file_with_wrong_bytes_is_downloaded_again()
    {
        var game = Content("game", 2_000);
        var build = new ManifestBuild().Chunk(1).File("game.bin", game);
        Publish(build);
        Directory.CreateDirectory(GameRoot);
        await File.WriteAllBytesAsync(Path.Combine(GameRoot, "game.bin"), new byte[game.Length]);
        new UbisoftInstallProgress { ProductId = Product, ManifestId = build.ManifestId, Fresh = true, Completed = { "game.bin" } }
            .Save(GameRoot);

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.All(build.SlicesOf("game.bin"), path => Assert.Equal(1, _cdn.Count(path)));
        Assert.Equal(game, await File.ReadAllBytesAsync(Path.Combine(GameRoot, "game.bin")));
    }

    [Fact]
    public async Task A_deleted_partial_file_is_downloaded_again()
    {
        var game = Content("game", 2_000);
        var build = new ManifestBuild().Chunk(1).File("game.bin", game);
        Publish(build);
        Directory.CreateDirectory(GameRoot);
        new UbisoftInstallProgress
        {
            ProductId = Product, ManifestId = build.ManifestId, Fresh = true,
            Partial = { ["game.bin"] = [0] }
        }.Save(GameRoot);

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.All(build.SlicesOf("game.bin"), path => Assert.Equal(1, _cdn.Count(path)));
    }

    [Fact]
    public async Task Cancelling_preserves_a_user_file_at_an_unselected_manifest_path()
    {
        var selected = Content("selected", 1_000);
        var english = Content("english", 1_000);
        var unselected = Content("unselected", 1_000);
        var build = new ManifestBuild().Chunk(1).File("selected.bin", selected);
        build.Chunk(2, "en-US").File("english.bin", english);
        build.Chunk(3, "fr-FR").File("french.bin", unselected);
        Publish(build);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slice = build.SlicesOf("selected.bin")[0];
        _cdn.Before = async (path, ct) =>
        {
            if (path != slice) return;
            blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var finished = Finished(ActionType.Install);
        Assert.True(_manager.TryAddToQueue(
            new InstallItem(App, ActionType.Install, GameRoot) { Languages = ["en-US"] }));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var userFile = Path.Combine(GameRoot, "french.bin");
        await File.WriteAllTextAsync(userFile, "mine");

        _manager.CancelInstall(App);
        var result = await finished;

        Assert.Equal(ActionStatus.Cancelled, result.Status);
        Assert.Equal("mine", await File.ReadAllTextAsync(userFile));
    }

    [Fact]
    public async Task Install_resumes_from_its_progress_file_without_fetching_finished_files()
    {
        var done = Content("done", 2_000);
        var todo = Content("todo", 2_000);
        var build = new ManifestBuild().Chunk(1).File("done.bin", done).File("todo.bin", todo);
        Publish(build);
        Directory.CreateDirectory(GameRoot);
        File.WriteAllBytes(Path.Combine(GameRoot, "done.bin"), done);
        new UbisoftInstallProgress { ProductId = Product, ManifestId = build.ManifestId, Fresh = true, Completed = { "done.bin" } }
            .Save(GameRoot);

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.All(build.SlicesOf("done.bin"), path => Assert.Equal(0, _cdn.Count(path)));
        Assert.All(build.SlicesOf("todo.bin"), path => Assert.Equal(1, _cdn.Count(path)));
        Assert.Equal(todo, File.ReadAllBytes(Path.Combine(GameRoot, "todo.bin")));
    }

    [Fact]
    public async Task Cancelling_a_fresh_install_removes_only_the_files_it_created()
    {
        var build = new ManifestBuild().Chunk(1).File("a.bin", Content("a", 1_000)).File(@"sub\b.bin", Content("b", 1_000));
        Publish(build);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slice = build.SlicesOf(@"sub\b.bin")[0];
        _cdn.Before = async (path, ct) =>
        {
            if (path != slice) return;
            blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var finished = Finished(ActionType.Install);
        Assert.True(_manager.TryAddToQueue(new InstallItem(App, ActionType.Install, GameRoot)));
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Something the user put there while the install was running is not the install's to delete.
        File.WriteAllText(Path.Combine(GameRoot, "readme-user.txt"), "keep");

        _manager.CancelInstall(App);
        var result = await finished;

        Assert.Equal(ActionStatus.Cancelled, result.Status);
        Assert.Equal(new[] { "readme-user.txt" }, Directory.EnumerateFileSystemEntries(GameRoot).Select(Path.GetFileName));
        Assert.Null(_store.Find(Product));
    }

    [Fact]
    public async Task A_slice_that_keeps_failing_its_hash_fails_the_install_after_retries()
    {
        var build = new ManifestBuild().Chunk(1).File("a.bin", Content("a", 1_000));
        Publish(build);
        var slice = build.SlicesOf("a.bin")[0];
        _cdn.Tamper = (path, _) => path == slice ? "garbage"u8.ToArray() : null;

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Equal("slice_hash_mismatch", result.Failure?.Code);
        Assert.True(result.Failure!.CanRetry);
        Assert.Equal(UbisoftDownloadEngine.SliceAttempts, _cdn.Count(slice));
        Assert.Null(_store.Find(Product));
    }

    [Fact]
    public async Task A_corrupt_or_refused_slice_is_fetched_again_and_a_refusal_renews_the_signature()
    {
        var content = Content("a", 3_000);
        var build = new ManifestBuild().Chunk(1).File("a.bin", content, sliceSize: 1_000);
        Publish(build);
        var corrupt = build.SlicesOf("a.bin")[0];
        var refused = build.SlicesOf("a.bin")[1];
        _cdn.Tamper = (path, attempt) => path == corrupt && attempt == 1 ? "garbage"u8.ToArray() : null;
        _cdn.Fail = (path, attempt) => path == refused && attempt == 1
            ? new HttpRequestException("expired", null, HttpStatusCode.Forbidden) : null;

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(GameRoot, "a.bin")));
        Assert.Equal(2, _source.Signed.Count(path => path == refused));
    }

    [Fact]
    public async Task Declined_system_setup_keeps_the_game_installed_and_pending_until_applied()
    {
        Publish(new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500)));
        _setup.Approve = false;
        var notices = new ConcurrentBag<OperationSnapshot>();
        _manager.OperationError += snapshot => { if (!snapshot.ShowErrorDialog) notices.Add(snapshot); };

        var result = await Run(new InstallItem(App, ActionType.Install, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.True(_store.Find(Product)!.SystemSetupPending);
        Assert.Contains(notices, notice => notice.Failure?.Code == "ubisoft_system_setup_pending");
        var request = Assert.Single(_setup.Requests);
        Assert.Contains(request.Writes, write =>
            write.Key == @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\Installs\4242\InstallDir" &&
            write.StringValue == GameRoot.Replace('\\', '/') + "/" && !write.BothViews);

        // The launch path offers it again.
        _setup.Approve = true;
        Assert.True(await _manager.PrepareLaunchAsync(Product, CancellationToken.None));
        Assert.False(_store.Find(Product)!.SystemSetupPending);
    }

    [Fact]
    public async Task Launch_points_the_launcher_at_the_runtime_only_while_something_else_holds_it()
    {
        Publish(new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500)));
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);
        Assert.All(_setup.Requests, request => Assert.Null(request.LauncherInstallDir));

        // Ubisoft Connect ran and took its folder back.
        _registry.LauncherDirectory = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\";
        _registry.Present = true;
        var before = _setup.Requests.Count;
        Assert.True(await _manager.PrepareLaunchAsync(Product, CancellationToken.None));
        var request = Assert.Single(_setup.Requests.Skip(before));
        Assert.Equal(Runtime, request.LauncherInstallDir);
        Assert.Empty(request.Runs);
        Assert.Equal([UiStrings.UbisoftElevationLauncherFolder], request.Describe());

        _registry.LauncherDirectory = Runtime;
        Assert.True(await _manager.PrepareLaunchAsync(Product, CancellationToken.None));
        Assert.Equal(before + 1, _setup.Requests.Count); // already in place: no prompt
    }

    [Fact]
    public async Task A_pause_cannot_hold_finalizing_which_keeps_running()
    {
        var build = new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500));
        Publish(build);
        var slice = build.SlicesOf("game.exe")[0];
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdn.Before = async (path, _) =>
        {
            if (path != slice) return;
            fetching.TrySetResult();
            await release.Task;
        };
        var inSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _setup.Before = async () =>
        {
            inSetup.TrySetResult();
            await finishSetup.Task;
        };
        var snapshots = new ConcurrentQueue<OperationSnapshot>();
        _manager.OperationUpdated += snapshots.Enqueue;
        var finished = Finished(ActionType.Install);
        Assert.True(_manager.TryAddToQueue(new InstallItem(App, ActionType.Install, GameRoot)));

        // The user pauses while the last slice is already on its way: the transfer completes regardless.
        await fetching.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _manager.PauseInstall();
        release.SetResult();
        await inSetup.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ActionStatus.Processing, _manager.CurrentOperation?.Status);
        // System setup is waiting on the administrator prompt; it cannot be paused.
        _manager.PauseInstall();
        Assert.Equal(ActionStatus.Processing, _manager.CurrentOperation?.Status);
        finishSetup.SetResult();

        Assert.Equal(ActionStatus.Success, (await finished).Status);
        Assert.DoesNotContain(snapshots, snapshot =>
            snapshot.Phase == OperationPhase.Finalizing && snapshot.Status == ActionStatus.Paused);
    }

    [Fact]
    public async Task Update_downloads_changed_files_and_deletes_only_files_leaving_the_selection()
    {
        var same = Content("same", 2_000);
        var first = new ManifestBuild().Chunk(1).File("same.bin", same).File("changed.bin", Content("old", 1_000))
            .File("removed.bin", Content("gone", 1_000));
        Publish(first);
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);
        File.WriteAllText(Path.Combine(GameRoot, "user.cfg"), "settings");

        var changed = Content("new", 1_500);
        var added = Content("added", 700);
        var second = new ManifestBuild().Chunk(1).File("same.bin", same).File("changed.bin", changed).File("added.bin", added);
        Publish(second);
        var result = await Run(new InstallItem(App, ActionType.Update, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.All(second.SlicesOf("same.bin"), path => Assert.Equal(1, _cdn.Count(path)));
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(GameRoot, "changed.bin")));
        Assert.Equal(added, File.ReadAllBytes(Path.Combine(GameRoot, "added.bin")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "removed.bin")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(GameRoot, "user.cfg")));
        Assert.Equal(second.ManifestId, _store.Find(Product)!.ManifestId);
    }

    [Fact]
    public async Task Verify_detects_a_damaged_slice_and_repair_restores_it()
    {
        var content = Content("data", 8_000);
        Publish(new ManifestBuild().Chunk(1).File("data.big", content, sliceSize: 2_000));
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);
        var path = Path.Combine(GameRoot, "data.big");
        using (var stream = File.OpenWrite(path))
        {
            stream.Position = 5_000; // same size, one byte different in the third slice
            stream.WriteByte((byte)(content[5_000] ^ 0xFF));
        }

        var verify = await Run(new InstallItem(App, ActionType.Verify, GameRoot));
        Assert.Equal(ActionStatus.Failed, verify.Status);
        Assert.Equal(OperationFailureKind.Verification, verify.Failure?.Kind);
        Assert.True(_store.Find(Product)!.NeedsRepair);

        var repair = await Run(new InstallItem(App, ActionType.Repair, GameRoot));
        Assert.Equal(ActionStatus.Success, repair.Status);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(_store.Find(Product)!.NeedsRepair);
    }

    [Fact]
    public async Task Changing_language_adds_the_new_language_and_removes_files_only_the_old_one_used()
    {
        const uint regionPack = 3671; // language chunks are gated on owned region packages
        _products.Owned.Add(regionPack);
        var shared = Content("shared", 600);
        var french = Content("fr", 800);
        Publish(new ManifestBuild()
            .Chunk(1).File("base.bin", Content("base", 900))
            .Chunk(2, "en-US").File("en.bin", Content("en", 700)).File("shared.bin", shared)
            .Chunk(3, "fr-FR", regionPack).File("fr.bin", french).File("shared.bin", shared));
        Assert.Equal(ActionStatus.Success,
            (await Run(new InstallItem(App, ActionType.Install, GameRoot) { Languages = ["en-US"] })).Status);
        Assert.True(File.Exists(Path.Combine(GameRoot, "en.bin")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "fr.bin")));

        var result = await Run(new InstallItem(App, ActionType.Repair, GameRoot) { Languages = ["fr-FR"] });

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.False(File.Exists(Path.Combine(GameRoot, "en.bin")));
        Assert.Equal(french, File.ReadAllBytes(Path.Combine(GameRoot, "fr.bin")));
        Assert.Equal(shared, File.ReadAllBytes(Path.Combine(GameRoot, "shared.bin")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "base.bin")));
        Assert.Equal(new[] { "fr-FR" }, _store.Find(Product)!.Languages);
        Assert.Contains(_setup.Requests[^1].Writes, write =>
            write.Key.EndsWith(@"Launcher\Installs\4242\Language", StringComparison.Ordinal) && write.StringValue == "fr-FR");
    }

    [Fact]
    public async Task Uninstall_removes_the_installation_and_leaves_foreign_files()
    {
        Publish(new ManifestBuild().Chunk(1).File(@"bin\game.exe", Content("game", 500)).File(@"data\a.big", Content("a", 500)));
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);
        Directory.CreateDirectory(Path.Combine(GameRoot, "saves"));
        File.WriteAllText(Path.Combine(GameRoot, "saves", "user.sav"), "progress");

        var result = await Run(new InstallItem(App, ActionType.Uninstall, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.Equal(new[] { "saves" }, Directory.EnumerateFileSystemEntries(GameRoot).Select(Path.GetFileName));
        Assert.Equal("progress", File.ReadAllText(Path.Combine(GameRoot, "saves", "user.sav")));
        Assert.Null(_store.Find(Product));
        Assert.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\Installs\4242\", _setup.Requests[^1].DeleteKeys);
    }

    [Theory]
    [InlineData(true, false, true)]   // last game, launcher points at Crimson: removed
    [InlineData(false, false, false)] // another Ubisoft game still installed: kept
    [InlineData(true, true, false)]   // a real Ubisoft Connect owns the value: never touched
    public async Task Uninstall_removes_the_launcher_folder_only_with_the_last_game_and_only_when_it_is_crimsons(
        bool last, bool realClient, bool removed)
    {
        Publish(new ManifestBuild().Chunk(1).File("game.exe", Content("game", 500)));
        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Install, GameRoot))).Status);
        if (!last)
            await _store.SaveAsync(new UbisoftInstalledGame { ProductId = 7, InstallPath = Path.Combine(_folder, "other") });
        _registry.LauncherDirectory = realClient ? @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\" : Runtime;

        Assert.Equal(ActionStatus.Success, (await Run(new InstallItem(App, ActionType.Uninstall, GameRoot))).Status);

        Assert.Equal(removed, _setup.Requests[^1].RemoveLauncherInstallDir);
        Assert.Equal(removed, _setup.Requests[^1].Describe().Contains(UiStrings.UbisoftElevationRemoveLauncherFolder));
    }

    [Fact]
    public async Task Import_of_differing_files_records_the_game_and_queues_verification()
    {
        var good = Content("good", 1_000);
        Publish(new ManifestBuild().Chunk(1).File("good.bin", good).File("stale.bin", Content("stale", 1_000)));
        Directory.CreateDirectory(GameRoot);
        File.WriteAllBytes(Path.Combine(GameRoot, "good.bin"), good);
        File.WriteAllBytes(Path.Combine(GameRoot, "stale.bin"), new byte[10]);
        var verify = Finished(ActionType.Verify);

        Assert.Null(await _manager.ValidateImportLocation(App, GameRoot));
        var result = await Run(new InstallItem(App, ActionType.Import, GameRoot));

        Assert.Equal(ActionStatus.Success, result.Status);
        Assert.NotNull(_store.Find(Product)!.ManifestId);
        Assert.True(_store.Find(Product)!.NeedsRepair);
        Assert.Equal(ActionStatus.Failed, (await verify).Status);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Evil")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Ubisoft\Game\InstallDir")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\..\Microsoft\Value")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\InstallDir")]
    public void Elevated_setup_rejects_keys_outside_a_game_s_ubisoft_keys(string key)
    {
        Assert.NotEmpty(UbisoftSystemSetupPolicy.Validate(Request([new UbisoftRegistryWrite(key, "x", null)])));
        Assert.NotEmpty(UbisoftSystemSetupPolicy.Validate(new UbisoftSystemSetupRequest([], [key + "\\"], [], null)));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"C:\Games\WD2")]
    [InlineData(@"C:\Games\WD2\..\evil.exe", @"C:\Games\WD2")]
    [InlineData(@"C:\Games\WD2 Evil\setup.exe", @"C:\Games\WD2 Evil")]
    [InlineData(@"C:\Games\WD2\Support\notes.txt", @"C:\Games\WD2")]
    public void Elevated_setup_runs_only_programs_inside_the_registered_installation(string executable, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var valid = Request([InstallDir(@"C:/Games/WD2/")],
            [new UbisoftSystemRun(@"C:\Games\WD2\Support\vcredist.exe", @"C:\Games\WD2\Support", "/quiet", "VC", false)]);
        Assert.Empty(UbisoftSystemSetupPolicy.Validate(valid));

        var invalid = Request([InstallDir(@"C:/Games/WD2/")],
            [new UbisoftSystemRun(executable, workingDirectory, "", "x", false)]);
        Assert.NotEmpty(UbisoftSystemSetupPolicy.Validate(invalid));
        // Without a registered installation nothing may run at all.
        Assert.NotEmpty(UbisoftSystemSetupPolicy.Validate(valid with { Writes = [] }));
    }

    [Fact]
    public void Elevated_setup_only_points_the_launcher_at_a_crimson_runtime_folder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Empty(UbisoftSystemSetupPolicy.Validate(
            new UbisoftSystemSetupRequest([], [], [], @"C:\Apps\Crimson\Assets\UbisoftRuntime")));
        Assert.NotEmpty(UbisoftSystemSetupPolicy.Validate(new UbisoftSystemSetupRequest([], [], [], @"C:\Games\Elsewhere")));
    }

    [Fact]
    public void Install_setup_substitutes_the_folder_picks_the_language_and_passes_the_helper_policy()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string root = @"C:\Games\Test Game";
        var manifest = UbisoftManifest.Parse(UbisoftManifestTests.Container(Message(
            Bytes(2, Message(Text(1, @"$INSTALL_DIR\Support\setup.exe"), Text(2, @"$INSTALL_DIR\Support"),
                Text(3, "/quiet"), Text(4, "Runtime"), Text(5, "VC"), Integer(6, 2))),
            Bytes(5, Message(Text(1, @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\TestGame\InstallDir"), Integer(2, 0),
                Bytes(3, Message(Text(1, "$INSTALL_DIR"))))),
            Bytes(5, Message(Text(1, @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\TestGame\Language"), Integer(2, 0),
                Bytes(3, Message(Text(1, "english"), Text(2, "en-US"))),
                Bytes(3, Message(Text(1, "french"), Text(2, "fr-FR"))))))));

        var (request, runKeys) = UbisoftSystemSetupRequests.ForInstall(Product, root, manifest, ["fr-FR"], [], includeRuns: true);

        Assert.Empty(UbisoftSystemSetupPolicy.Validate(request));
        Assert.Contains(request.Writes, write => write.Key == @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\TestGame\InstallDir" &&
            write.StringValue == root && write.BothViews);
        Assert.Contains(request.Writes, write => write.Key == @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\TestGame\Language" &&
            write.StringValue == "french");
        var run = Assert.Single(request.Runs);
        Assert.Equal(@"C:\Games\Test Game\Support\setup.exe", run.Executable);
        Assert.Equal(new[] { "VC:2" }, runKeys);

        var (again, _) = UbisoftSystemSetupRequests.ForInstall(Product, root, manifest, ["fr-FR"], ["VC:2"], includeRuns: true);
        Assert.Empty(again.Runs);
    }

    [Fact]
    public async Task Installed_store_migrates_legacy_configuration_games_once()
    {
        Directory.CreateDirectory(ProviderRoot);
        var legacyPath = Path.Combine(_folder, "Old Games", "WD2");
        File.WriteAllText(Path.Combine(ProviderRoot, "configuration.json"), JsonSerializer.Serialize(new
        {
            Games = new object[]
            {
                new { ProductId = 2688, InstallDirectory = legacyPath + Path.DirectorySeparatorChar, Language = "fr-FR" },
                new { ProductId = 0, InstallDirectory = legacyPath, Language = "en-US" },
                new { ProductId = 77, InstallDirectory = "relative\\path", Language = "en-US" }
            }
        }));

        var game = Assert.Single(await new UbisoftInstalledStore(ProviderRoot).LoadAsync());

        Assert.Equal(2688u, game.ProductId);
        Assert.Equal(legacyPath, game.InstallPath);
        Assert.Equal(new[] { "fr-FR" }, game.Languages);
        Assert.Null(game.ManifestId);
        Assert.True(File.Exists(Path.Combine(ProviderRoot, "installed.json")));

        // installed.json is the source of truth from now on.
        File.Delete(Path.Combine(ProviderRoot, "configuration.json"));
        Assert.Equal(2688u, Assert.Single(await new UbisoftInstalledStore(ProviderRoot).LoadAsync()).ProductId);
    }

    private void Publish(ManifestBuild build)
    {
        var manifestId = build.ManifestId; // builds the manifest object
        foreach (var (path, bytes) in build.Objects) _cdn.Objects[path] = bytes;
        _products.Products[Product] = new UbisoftOwnedProduct(Product, true, UbisoftOwnedProduct.TypeGame,
            UbisoftOwnedProduct.StateReleased, Product, 1, null, manifestId, 0, [], [], null, null, 0, 0, 0, 0, false, 0);
    }

    private async Task<OperationSnapshot> Run(InstallItem item)
    {
        var finished = Finished(item.Action);
        Assert.True(_manager.TryAddToQueue(item));
        return await finished;
    }

    private Task<OperationSnapshot> Finished(ActionType action)
    {
        var done = new TaskCompletionSource<OperationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.OperationUpdated += snapshot =>
        {
            if (snapshot.Action == action && snapshot.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
                done.TrySetResult(snapshot);
        };
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static UbisoftSystemSetupRequest Request(IReadOnlyList<UbisoftRegistryWrite> writes,
        IReadOnlyList<UbisoftSystemRun>? runs = null) => new(writes, [], runs ?? [], null);

    private static UbisoftRegistryWrite InstallDir(string path) =>
        new(@"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\Launcher\Installs\2688\InstallDir", path, null, BothViews: false);

    private static byte[] Content(string seed, int length)
    {
        var bytes = new byte[length];
        var pattern = Encoding.ASCII.GetBytes(seed + " repeats; ");
        for (var index = 0; index < length; index++) bytes[index] = pattern[index % pattern.Length];
        return bytes;
    }

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class ManifestBuild(UbisoftCompression compression = UbisoftCompression.Zstd)
    {
        private readonly List<(uint Id, string? Language, uint[] UplayIds, List<byte[]> Files)> _chunks = [];
        private readonly Dictionary<string, List<string>> _slices = new(StringComparer.OrdinalIgnoreCase);
        private string? _manifestId;

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public string ManifestId
        {
            get
            {
                if (_manifestId is not null) return _manifestId;
                var container = UbisoftManifestTests.Container(Message([
                    .. _chunks.Select(chunk => UbisoftManifestTests.Chunk(chunk.Id, false, chunk.Language, null,
                        chunk.UplayIds, [.. chunk.Files])),
                    Integer(17, compression == UbisoftCompression.None ? 0UL : 1UL),
                    Integer(21, (ulong)compression),
                    Integer(22, 3)
                ]));
                _manifestId = UbisoftManifest.ComputeId(container);
                Objects[$"manifests/{_manifestId}.manifest"] = container;
                return _manifestId;
            }
        }

        public ManifestBuild Chunk(uint id, string? language = null, params uint[] uplayIds)
        {
            _chunks.Add((id, language, uplayIds, []));
            return this;
        }

        public ManifestBuild Directory(string path)
        {
            _chunks[^1].Files.Add(UbisoftManifestTests.FileEntry(path, 0, [], directory: true));
            return this;
        }

        public ManifestBuild File(string path, byte[] content, int sliceSize = 4096, Func<int, bool>? stored = null)
        {
            var hashes = new List<byte[]>();
            var slices = new List<byte[]>();
            var objects = new List<string>();
            for (int offset = 0, index = 0; offset < content.Length; offset += sliceSize, index++)
            {
                var piece = content.AsSpan(offset, Math.Min(sliceSize, content.Length - offset)).ToArray();
                var encoded = stored?.Invoke(index) == true ? piece : Encode(piece);
                var sha1 = SHA1.HashData(encoded);
                var objectPath = UbisoftManifest.SlicePath(Convert.ToHexString(sha1));
                Objects[objectPath] = encoded;
                objects.Add(objectPath);
                hashes.Add(SHA1.HashData(piece));
                slices.Add(UbisoftManifestTests.Slice((uint)piece.Length, (uint)encoded.Length, sha1, (ulong)offset));
            }
            _slices[path] = objects;
            _chunks[^1].Files.Add(UbisoftManifestTests.FileEntry(path, (ulong)content.Length, [.. hashes], [.. slices]));
            return this;
        }

        public IReadOnlyList<string> SlicesOf(string path) => _slices[path];

        private byte[] Encode(byte[] data)
        {
            switch (compression)
            {
                case UbisoftCompression.Zstd:
                {
                    using var compressor = new Compressor();
                    return compressor.Wrap(data).ToArray();
                }
                case UbisoftCompression.Deflate:
                {
                    using var output = new MemoryStream();
                    using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
                    return output.ToArray();
                }
                default:
                    return data;
            }
        }
    }

    private sealed class FakeProducts : IUbisoftProductSource
    {
        public ConcurrentDictionary<uint, UbisoftOwnedProduct> Products { get; } = new();
        public HashSet<uint> Owned { get; } = [];

        public Task<UbisoftOwnedProduct?> GetProduct(uint productId, CancellationToken ct) =>
            Task.FromResult(Products.GetValueOrDefault(productId));

        public Task<IReadOnlySet<uint>> GetOwnedIds(CancellationToken ct) => Task.FromResult<IReadOnlySet<uint>>(Owned.ToHashSet());

        public string? GetTitle(uint productId) => "Test Game";
    }

    private sealed class FakeSource : IUbisoftContentSource
    {
        private int _signature;

        public ConcurrentQueue<string> Signed { get; } = new();
        public TimeSpan UrlLifetime => TimeSpan.FromHours(1);

        public Task<IReadOnlyList<IReadOnlyList<string>>> GetUrlsAsync(IReadOnlyList<string> relativePaths, CancellationToken ct)
        {
            foreach (var path in relativePaths) Signed.Enqueue(path);
            return Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(relativePaths
                .Select(path => (IReadOnlyList<string>)[$"http://cdn.test/{path}?sig={Interlocked.Increment(ref _signature)}"])
                .ToList());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCdn : IUbisoftCdn
    {
        private const string Prefix = "http://cdn.test/";
        private readonly ConcurrentDictionary<string, int> _requests = new();

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public Func<string, int, byte[]?>? Tamper { get; set; }
        public Func<string, int, Exception?>? Fail { get; set; }
        public Func<string, CancellationToken, Task>? Before { get; set; }

        public int Count(string path) => _requests.GetValueOrDefault(path);

        public async Task<byte[]> GetAsync(string url, long maximumBytes, Action<long>? onBytes, CancellationToken cancellationToken)
        {
            var path = url[Prefix.Length..url.IndexOf('?')];
            var attempt = _requests.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (Before is { } before) await before(path, cancellationToken);
            if (Fail?.Invoke(path, attempt) is { } failure) throw failure;
            if (!Objects.TryGetValue(path, out var body)) throw new HttpRequestException("missing", null, HttpStatusCode.NotFound);
            body = Tamper?.Invoke(path, attempt) ?? body;
            onBytes?.Invoke(body.Length);
            return body;
        }
    }

    private sealed class FakeSetup : IUbisoftSystemSetup
    {
        public bool Approve { get; set; } = true;
        public Func<Task>? Before { get; set; }
        public List<UbisoftSystemSetupRequest> Requests { get; } = [];

        public async Task<UbisoftSystemSetupResult> ApplyAsync(UbisoftSystemSetupRequest request, CancellationToken ct)
        {
            if (Before is { } before) await before();
            lock (Requests) Requests.Add(request);
            return Approve
                ? new UbisoftSystemSetupResult(true, request.Runs.Select(run => new UbisoftSystemRunResult(run.Description, 0)).ToList(), null)
                : new UbisoftSystemSetupResult(false, [], "declined");
        }
    }

    private sealed class FakeRegistry : IUbisoftClientRegistry
    {
        public Dictionary<uint, UbisoftClientInstall> Installs { get; } = [];
        public string? LauncherDirectory { get; set; }
        /// <summary>Whether the game's registry values already exist (a launch then needs only the launcher folder).</summary>
        public bool Present { get; set; }

        public IReadOnlyDictionary<uint, UbisoftClientInstall> ReadInstalls() => Installs;
        public bool HasValue(UbisoftRegistryWrite write) => Present;
        public bool Exists(string deletePath) => true;
        public bool LauncherNeedsDirectory(string directory) => !string.Equals(
            LauncherDirectory?.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        public bool LauncherHasCrimsonDirectory() => UbisoftSystemSetupPolicy.IsCrimsonRuntimeDirectory(LauncherDirectory);
    }
}
