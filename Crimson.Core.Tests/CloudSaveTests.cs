using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class CloudSaveTests : IDisposable
{
    private const string AppName = "cloud-game";
    private const string AccountId = "synthetic-account";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-cloud-{Guid.NewGuid():N}");
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly Storage _storage;
    private readonly FakeCloudRepository _repository = new();
    private readonly CloudSaveManager _manager;

    public CloudSaveTests()
    {
        _storage = new Storage(_logger, Path.Combine(_root, "data"), Path.Combine(_root, "games"));
        _storage.SaveUserData(new UserData { AccountId = AccountId }).Wait();
        _manager = new CloudSaveManager(_logger, _repository, _storage);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PackageRoundTripsThroughManifestAndChunkReaders()
    {
        var saveDir = CreateSaveDirectory(("save/main.sav", 300_000), ("save/secondary.sav", 100), ("save/deep/nested.sav", 2_000_000));
        var timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

        var files = SaveGamePacker.Package(saveDir, AppName, AccountId, "{installdir}/save", null, null, timestamp);

        var manifestKey = Assert.Single(files.Keys, key => key.StartsWith("manifests/", StringComparison.Ordinal));
        Assert.Equal("manifests/2024.05.06-07.08.09.manifest", manifestKey);
        var chunkKeys = files.Keys.Where(key => key.EndsWith(".chunk", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, chunkKeys.Count); // 2.3 MiB of saves span three 1 MiB windows

        var manifest = Manifest.ReadAll(files[manifestKey]);
        Assert.Equal(AppName + AccountId, manifest.ManifestMeta.AppName);
        Assert.Equal("2024.05.06-07.08.09", manifest.ManifestMeta.BuildVersion);
        Assert.Equal("{installdir}/save", manifest.CustomFields["CloudSaveFolder"]);
        Assert.Equal(3, manifest.CDL.Elements.Count);

        var chunksByPath = chunkKeys.ToDictionary(key => key, key => Chunk.ReadBuffer(files[key]));
        foreach (var chunkInfo in manifest.CDL.Elements)
        {
            var chunk = Assert.Single(chunksByPath, pair => pair.Key == chunkInfo.Path).Value;
            chunk.ValidateAgainst(chunkInfo);
        }

        Assert.Equal(
            new[] { "save/deep/nested.sav", "save/main.sav", "save/secondary.sav" },
            manifest.FileManifestList.Elements.Select(file => file.Filename));
        foreach (var file in manifest.FileManifestList.Elements)
        {
            using var reassembled = new MemoryStream();
            foreach (var part in file.ChunkParts)
            {
                var chunkInfo = manifest.CDL.GetChunkByGuidNum(part.GuidNum);
                var data = chunksByPath[chunkInfo.Path].Data;
                reassembled.Write(data, part.Offset, part.Size);
            }

            var original = File.ReadAllBytes(Path.Combine(saveDir, file.Filename.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(SHA1.HashData(original), file.Hash);
            Assert.Equal(original, reassembled.ToArray());
        }
    }

    [Fact]
    public void PackageAppliesIncludeAndExcludeFilters()
    {
        var saveDir = CreateSaveDirectory(("keep/one.sav", 10), ("keep/two.txt", 10), ("drop/three.sav", 10));

        var files = SaveGamePacker.Package(saveDir, AppName, AccountId, "{installdir}", ["*.sav"], ["drop/"], DateTimeOffset.UtcNow);

        var manifest = Manifest.ReadAll(files[Assert.Single(files.Keys, key => key.EndsWith(".manifest", StringComparison.Ordinal))]);
        Assert.Equal(["keep/one.sav"], manifest.FileManifestList.Elements.Select(file => file.Filename));
    }

    [Fact]
    public void PackageReturnsEmptyWhenFiltersMatchNothing()
    {
        var saveDir = CreateSaveDirectory(("notes.txt", 10));
        Assert.Empty(SaveGamePacker.Package(saveDir, AppName, AccountId, "{installdir}", ["*.sav"], null, DateTimeOffset.UtcNow));
        Assert.Empty(SaveGamePacker.Package(Path.Combine(_root, "missing"), AppName, AccountId, "{installdir}", null, null, DateTimeOffset.UtcNow));
    }

    [Theory]
    // same age within 60 seconds
    [InlineData(30, CloudSaveState.SameAge)]
    [InlineData(-30, CloudSaveState.SameAge)]
    [InlineData(120, CloudSaveState.LocalNewer)]
    [InlineData(-120, CloudSaveState.RemoteNewer)]
    public void CheckStateComparesTimestampsWithTolerance(int localOffsetSeconds, CloudSaveState expected)
    {
        var remoteTime = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var remote = new RemoteSaveInfo(AppName, $"savesync/{AccountId}/{AppName}/manifests/2024.01.01-12.00.00.manifest", "2024.01.01-12.00.00.manifest", remoteTime);
        var local = new LocalSaveInfo(remoteTime.AddSeconds(localOffsetSeconds), 10, 1);
        Assert.Equal(expected, CloudSaveManager.CheckState(local, remote));
    }

    [Fact]
    public void CheckStateHandlesMissingSides()
    {
        var remote = new RemoteSaveInfo(AppName, "path/manifests/2024.01.01-12.00.00.manifest", "2024.01.01-12.00.00.manifest", DateTimeOffset.UtcNow);
        Assert.Equal(CloudSaveState.NoSave, CloudSaveManager.CheckState(default, null));
        Assert.Equal(CloudSaveState.LocalNewer, CloudSaveManager.CheckState(new LocalSaveInfo(DateTimeOffset.UtcNow, 10, 1), null));
        Assert.Equal(CloudSaveState.RemoteNewer, CloudSaveManager.CheckState(default, remote));
    }

    [Fact]
    public void GetLatestRemoteSavePicksNewestManifest()
    {
        var files = new Dictionary<string, CloudSaveFileEntry>(StringComparer.Ordinal)
        {
            [$"savesync/{AccountId}/{AppName}/manifests/2024.01.01-10.00.00.manifest"] = new(),
            [$"savesync/{AccountId}/{AppName}/manifests/2024.01.03-10.00.00.manifest"] = new(),
            [$"savesync/{AccountId}/{AppName}/manifests/2024.01.02-10.00.00.manifest"] = new(),
            [$"savesync/{AccountId}/{AppName}/ChunksV4/00/chunk.chunk"] = new(),
            [$"savesync/{AccountId}/other-game/manifests/2025.01.01-10.00.00.manifest"] = new(),
            ["bogus"] = new()
        };

        var latest = CloudSaveManager.GetLatestRemoteSave(AppName, files);

        Assert.NotNull(latest);
        Assert.Equal("2024.01.03-10.00.00.manifest", latest.ManifestName);
        Assert.Equal(new DateTimeOffset(2024, 1, 3, 10, 0, 0, TimeSpan.Zero), latest.TimestampUtc);
        Assert.Null(CloudSaveManager.GetLatestRemoteSave(AppName, new Dictionary<string, CloudSaveFileEntry>()));
        Assert.Null(CloudSaveManager.GetLatestRemoteSave(AppName, null));
    }

    [Fact]
    public void ResolveSavePathSubstitutesKnownVariables()
    {
        var installPath = Path.Combine(_root, "game");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installPath, "Saves")),
            CloudSaveManager.ResolveSavePath("{installdir}/Saves", installPath, AccountId));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installPath, "Saves", AccountId)),
            CloudSaveManager.ResolveSavePath(@"{installdir}\Saves\{epicid}", installPath, AccountId));
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(userProfile, "Saved Games", "Studio", "Game")),
            CloudSaveManager.ResolveSavePath("{usersavedgames}/Studio/Game", installPath, AccountId));
    }

    [Fact]
    public void ResolveSavePathRejectsUnresolvableVariables()
    {
        var installPath = Path.Combine(_root, "game");
        Assert.Null(CloudSaveManager.ResolveSavePath("{unknownvar}/Saves", installPath, AccountId));
        Assert.Null(CloudSaveManager.ResolveSavePath("{epicid}/Saves", installPath, null));
        Assert.Null(CloudSaveManager.ResolveSavePath("{epicid}/Saves", installPath, string.Empty));
    }

    [Fact]
    public async Task LaunchSyncDownloadsWhenOnlyCloudSaveExists()
    {
        var remoteTime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("profile/save.sav", 50_000));
        var game = CloudGame();
        var statuses = new List<CloudSaveSyncStatus>();
        _manager.SyncStatusChanged += (appName, status) => statuses.Add(status);

        await _manager.SyncBeforeLaunchAsync(game);

        var savePath = SavePath(game);
        var extracted = File.ReadAllBytes(Path.Combine(savePath, "profile", "save.sav"));
        Assert.Equal(RemoteSeedContent("profile/save.sav", 50_000), extracted);
        Assert.Equal(remoteTime.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(savePath, "profile", "save.sav")));
        Assert.Equal([CloudSaveSyncStatus.Checking, CloudSaveSyncStatus.Downloading, CloudSaveSyncStatus.Idle], statuses);
        Assert.Equal(CloudSaveSyncStatus.Idle, _manager.GetSyncStatus(AppName));
    }

    [Fact]
    public async Task LaunchSyncSkipsWhenSavesAreSameAge()
    {
        var timestamp = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        SeedRemoteSave(timestamp, ("save.sav", 1_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), RemoteSeedContent("save.sav", 1_000));
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), timestamp.UtcDateTime);
        var resolverCalls = 0;
        _manager.ConflictResolver = _ =>
        {
            resolverCalls++;
            return Task.FromResult(CloudSaveResolution.KeepLocal);
        };

        await _manager.SyncBeforeLaunchAsync(game);

        Assert.Equal(0, resolverCalls);
        Assert.Equal(1, _repository.ManifestListings); // state check only, nothing downloaded
        Assert.Equal(0, _repository.FileDownloads);
    }

    [Fact]
    public async Task LaunchSyncConflictKeepsTheCopyTheUserPicks()
    {
        var remoteTime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 2_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[3_000]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(1).UtcDateTime);

        CloudSaveConflict? shown = null;
        _manager.ConflictResolver = conflict =>
        {
            shown = conflict;
            return Task.FromResult(CloudSaveResolution.KeepLocal);
        };
        await _manager.SyncBeforeLaunchAsync(game);

        Assert.NotNull(shown);
        Assert.Equal(AppName, shown.AppName);
        Assert.Equal(3_000, shown.LocalSizeBytes);
        Assert.Equal(2_000, shown.RemoteSizeBytes);
        Assert.True(shown.LocalTimestampUtc > shown.RemoteTimestampUtc);
        Assert.Equal(3_000, new FileInfo(Path.Combine(savePath, "save.sav")).Length); // untouched

        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.KeepCloud);
        await _manager.SyncBeforeLaunchAsync(game);

        Assert.Equal(RemoteSeedContent("save.sav", 2_000), File.ReadAllBytes(Path.Combine(savePath, "save.sav")));
    }

    [Fact]
    public async Task FailedDownloadLeavesTheExistingLocalSaveUntouched()
    {
        var remoteTime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 2_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        var original = RemoteSeedContent("local-session", 3_000);
        var localPath = Path.Combine(savePath, "save.sav");
        File.WriteAllBytes(localPath, original);
        File.SetLastWriteTimeUtc(localPath, remoteTime.AddHours(1).UtcDateTime);
        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.KeepCloud);
        _repository.FailChunkDownloads = true;

        await _manager.SyncBeforeLaunchAsync(game);

        Assert.Equal(original, File.ReadAllBytes(localPath));
        AssertNoTemporarySaveDirectories(game);
    }

    [Fact]
    public async Task KeepCloudReplacesTheSaveAndRemovesFilesMissingFromTheManifest()
    {
        var remoteTime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 2_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        var localTime = remoteTime.AddHours(1).UtcDateTime;
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[3_000]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), localTime);
        var stale = Path.Combine(savePath, "stale.sav");
        File.WriteAllBytes(stale, new byte[100]);
        File.SetLastWriteTimeUtc(stale, localTime);
        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.KeepCloud);

        await _manager.SyncBeforeLaunchAsync(game);

        Assert.Equal(RemoteSeedContent("save.sav", 2_000), File.ReadAllBytes(Path.Combine(savePath, "save.sav")));
        Assert.False(File.Exists(stale));
        AssertNoTemporarySaveDirectories(game);
    }

    [Fact]
    public async Task WaitForIdleAsyncWaitsUntilTheRunningSyncReleasesItsGate()
    {
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[100]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), DateTimeOffset.UtcNow.AddHours(1).UtcDateTime);
        _repository.BlockManifestListings = true;

        var sync = _manager.SyncAfterExitAsync(game);
        await _repository.ManifestListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = _manager.WaitForIdleAsync(AppName);

        Assert.False(idle.IsCompleted);
        _repository.ReleaseManifestListing.SetResult();
        await sync.WaitAsync(TimeSpan.FromSeconds(5));
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GameStaysMarkedRunningUntilThePostExitSyncFinishes()
    {
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[100]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), DateTimeOffset.UtcNow.AddHours(1).UtcDateTime);
        using var http = new HttpClient();
        var auth = new AuthManager(_logger, _storage, new TestCredentialProtector(), http);
        var library = new LibraryManager(_logger, _repository, _storage, auth, _manager);
        library.SetGameRunning(AppName, true);
        _repository.BlockManifestListings = true;

        var exit = (Task)typeof(LibraryManager)
            .GetMethod("AfterGameExitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(library, [new CompletedHandle(), null, game, true])!;
        await _repository.ManifestListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(library.IsGameRunning(AppName));
        _repository.ReleaseManifestListing.SetResult();
        await exit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(library.IsGameRunning(AppName));
    }

    [Fact]
    public async Task ExitSyncUploadsWhenLocalSaveIsNewer()
    {
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        var localTime = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var content = RemoteSeedContent("save.sav", 4_000);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), content);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), localTime.UtcDateTime);

        await _manager.SyncAfterExitAsync(game);

        Assert.True(_repository.Uploads > 0);
        var remote = CloudSaveManager.GetLatestRemoteSave(AppName, _repository.Listing(AppName, manifestsOnly: true));
        Assert.NotNull(remote);
        Assert.Equal("2024.06.07-08.09.10.manifest", remote.ManifestName);

        // The uploaded save must round-trip: parse the manifest and reassemble the file.
        var manifest = Manifest.ReadAll(_repository.RemoteFiles[remote.Path]);
        var stored = Assert.Single(manifest.FileManifestList.Elements);
        Assert.Equal("save.sav", stored.Filename);
        using var reassembled = new MemoryStream();
        foreach (var part in stored.ChunkParts)
        {
            var chunkInfo = manifest.CDL.GetChunkByGuidNum(part.GuidNum);
            var chunkBytes = _repository.RemoteFiles[$"savesync/{AccountId}/{AppName}/{chunkInfo.Path}"];
            var data = Chunk.ReadBuffer(chunkBytes).Data;
            reassembled.Write(data, part.Offset, part.Size);
        }
        Assert.Equal(content, reassembled.ToArray());

        // After the upload a state check sees both sides as the same age.
        var local = CloudSaveManager.ScanLocalSave(game, savePath);
        Assert.Equal(CloudSaveState.SameAge, CloudSaveManager.CheckState(local, remote));
    }

    [Fact]
    public async Task ExitSyncAsksWhenCloudSaveIsNewer()
    {
        var remoteTime = new DateTimeOffset(2024, 8, 9, 10, 11, 12, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 7_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[5_000]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(-2).UtcDateTime);
        var uploadsBefore = _repository.Uploads;

        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.KeepLocal);
        await _manager.SyncAfterExitAsync(game);
        Assert.True(_repository.Uploads > uploadsBefore);

        uploadsBefore = _repository.Uploads;
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(-2).UtcDateTime);
        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.KeepCloud);
        await _manager.SyncAfterExitAsync(game);
        Assert.Equal(uploadsBefore, _repository.Uploads); // no upload; the cloud copy was downloaded instead
        Assert.Equal(7_000, new FileInfo(Path.Combine(savePath, "save.sav")).Length);

        uploadsBefore = _repository.Uploads;
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(-2).UtcDateTime);
        _manager.ConflictResolver = _ => Task.FromResult(CloudSaveResolution.Skip);
        await _manager.SyncAfterExitAsync(game);
        Assert.Equal(uploadsBefore, _repository.Uploads);
        // Skip leaves the previously downloaded cloud copy in place.
        Assert.Equal(7_000, new FileInfo(Path.Combine(savePath, "save.sav")).Length);
    }

    [Fact]
    public async Task ExitSyncSkipsUploadWhenTheRemoteListingFails()
    {
        // A failed listing must not look like "no remote saves": uploading blindly could
        // shadow a newer cloud manifest the listing would have revealed.
        var remoteTime = new DateTimeOffset(2024, 8, 9, 10, 11, 12, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 7_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[9_000]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(2).UtcDateTime);
        _repository.FailListings = true;

        await _manager.SyncAfterExitAsync(game);

        Assert.Equal(0, _repository.Uploads);
        Assert.Equal(CloudSaveSyncStatus.Idle, _manager.GetSyncStatus(AppName));
    }

    [Fact]
    public async Task LaunchSyncWithOnlyLocalSavesDoesNotAskOrDownload()
    {
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[1_000]);
        var resolverCalls = 0;
        _manager.ConflictResolver = _ =>
        {
            resolverCalls++;
            return Task.FromResult(CloudSaveResolution.KeepLocal);
        };

        await _manager.SyncBeforeLaunchAsync(game);

        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, _repository.FileDownloads);
        Assert.Equal(CloudSaveSyncStatus.Idle, _manager.GetSyncStatus(AppName));
    }

    [Fact]
    public async Task ExitSyncWithoutResolverSkipsConflicts()
    {
        var remoteTime = new DateTimeOffset(2024, 8, 9, 10, 11, 12, TimeSpan.Zero);
        SeedRemoteSave(remoteTime, ("save.sav", 7_000));
        var game = CloudGame();
        var savePath = SavePath(game);
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(Path.Combine(savePath, "save.sav"), new byte[5_000]);
        File.SetLastWriteTimeUtc(Path.Combine(savePath, "save.sav"), remoteTime.AddHours(-2).UtcDateTime);

        await _manager.SyncAfterExitAsync(game);

        Assert.Equal(0, _repository.Uploads);
    }

    [Fact]
    public async Task SyncIgnoresGamesWithoutCloudSaveSupport()
    {
        var game = CloudGame();
        game.Metadata.CustomAttributes!.CloudSaveFolder = null;

        await _manager.SyncBeforeLaunchAsync(game);
        await _manager.SyncAfterExitAsync(game);

        Assert.Equal(0, _repository.ManifestListings);
        Assert.Equal(0, _repository.Uploads);
    }

    private Game CloudGame(IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null)
    {
        var installPath = Path.Combine(_root, "game");
        Directory.CreateDirectory(installPath);
        return new Game
        {
            AppName = AppName,
            AppTitle = "Cloud Game",
            AssetInfos = new AssetInfos { Windows = new Asset { AppName = AppName, CatalogItemId = "id", Namespace = "ns", BuildVersion = "1" } },
            Metadata = new Metadata
            {
                Id = "id",
                CustomAttributes = new CustomAttributes
                {
                    CloudSaveFolder = new CustomAttributeValue { Value = "{installdir}/Saves" },
                    CloudIncludeList = include is null ? null : new CustomAttributeValue { Value = string.Join(',', include) },
                    CloudExcludeList = exclude is null ? null : new CustomAttributeValue { Value = string.Join(',', exclude) }
                }
            },
            LocalAppState = new LocalAppState { AppName = AppName, InstallStatus = InstallState.Installed, InstallPath = installPath }
        };
    }

    private string SavePath(Game game) => Path.Combine(game.LocalAppState!.InstallPath, "Saves");

    private static void AssertNoTemporarySaveDirectories(Game game)
    {
        var installPath = game.LocalAppState!.InstallPath;
        var leftovers = Directory.GetDirectories(installPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(".crimson-", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(leftovers);
    }

    private string CreateSaveDirectory(params (string RelativePath, int Size)[] files)
    {
        var directory = Path.Combine(_root, "saves", Guid.NewGuid().ToString("N"));
        foreach (var (relativePath, size) in files)
        {
            var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, RemoteSeedContent(relativePath, size));
        }

        return directory;
    }

    private void SeedRemoteSave(DateTimeOffset timestamp, params (string RelativePath, int Size)[] files)
    {
        var source = CreateSaveDirectory(files);
        var packaged = SaveGamePacker.Package(source, AppName, AccountId, "{installdir}/Saves", null, null, timestamp);
        foreach (var (path, bytes) in packaged)
            _repository.RemoteFiles[$"savesync/{AccountId}/{AppName}/{path}"] = bytes;
    }

    private static byte[] RemoteSeedContent(string name, int size)
    {
        var seed = SHA1.HashData(Encoding.UTF8.GetBytes(name));
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = seed[i % seed.Length];
        return bytes;
    }

    /// <summary>
    /// In-memory savesync backend. Listings return full remote paths like the real API;
    /// uploads address files through pre-signed-style links while reservations key entries
    /// by the relative paths that were posted, matching the savesync contract.
    /// </summary>
    private sealed class FakeCloudRepository : IStoreRepository
    {
        public readonly Dictionary<string, byte[]> RemoteFiles = new(StringComparer.Ordinal);
        public int ManifestListings;
        public int FileDownloads;
        public int Uploads;
        public bool FailListings;
        public bool FailChunkDownloads;
        public bool BlockManifestListings;
        public TaskCompletionSource ManifestListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseManifestListing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyDictionary<string, CloudSaveFileEntry> Listing(string appName, bool manifestsOnly) =>
            RemoteFiles.Keys
                .Where(path => path.StartsWith($"savesync/{AccountId}/{appName}/", StringComparison.Ordinal) &&
                               (!manifestsOnly || path.Contains("/manifests/", StringComparison.Ordinal)))
                .ToDictionary(
                    path => path,
                    path => new CloudSaveFileEntry
                    {
                        ReadLink = $"read://{path}",
                        WriteLink = $"write://{path}",
                        LastModified = "2024-01-01T00:00:00.000Z"
                    }, StringComparer.Ordinal);

        public async Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> GetCloudSaveFilesAsync(
            string appName, bool manifestsOnly, CancellationToken cancellationToken = default)
        {
            if (manifestsOnly) ManifestListings++;
            if (FailListings)
                return null;
            if (manifestsOnly && BlockManifestListings)
            {
                ManifestListingStarted.TrySetResult();
                await ReleaseManifestListing.Task;
            }
            return Listing(appName, manifestsOnly);
        }

        public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> CreateCloudSaveFilesAsync(
            string appName, IReadOnlyList<string> files, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CloudSaveFileEntry>?>(files.ToDictionary(
                path => path,
                path => new CloudSaveFileEntry { WriteLink = $"write://savesync/{AccountId}/{appName}/{path}" },
                StringComparer.Ordinal));

        public Task<byte[]?> DownloadCloudSaveFileAsync(string readLink, CancellationToken cancellationToken = default)
        {
            FileDownloads++;
            if (FailChunkDownloads && readLink.EndsWith(".chunk", StringComparison.Ordinal))
                throw new IOException("Synthetic chunk download failure");
            return Task.FromResult<byte[]?>(RemoteFiles[readLink["read://".Length..]]);
        }

        public Task<bool> UploadCloudSaveFileAsync(string writeLink, byte[] data, CancellationToken cancellationToken = default)
        {
            Uploads++;
            RemoteFiles[writeLink["write://".Length..]] = data;
            return Task.FromResult(true);
        }

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

    private sealed class CompletedHandle : IGameProcessHandle
    {
        public int Id => 4242;
        public Task Exited => Task.CompletedTask;
        public void Dispose() { }
    }
}
