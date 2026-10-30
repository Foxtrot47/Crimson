using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

/// <summary>
/// Two-way cloud save sync for games that declare the CloudSaveFolder custom attribute,
/// following Epic's savesync API (as implemented by legendary). Saves are downloaded
/// before a game starts and uploaded after it exits. When both sides exist but disagree
/// on timestamps the direction cannot be picked safely, so the user is asked through
/// <see cref="ConflictResolver"/>; without a resolver the safe choice (do nothing) is made.
/// Uploads reuse the local save timestamp as the manifest name so a subsequent check
/// sees both sides as the same age.
/// </summary>
public class CloudSaveManager
{
    private static readonly TimeSpan SameAgeTolerance = TimeSpan.FromSeconds(60);

    private readonly ILogger _log;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;
    private readonly ConcurrentDictionary<string, CloudSaveSyncStatus> _syncStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncGates = new(StringComparer.Ordinal);

    public event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;

    /// <summary>
    /// Set by the UI layer. Called on the sync path when local and cloud saves diverge;
    /// must return which copy survives.
    /// </summary>
    public Func<CloudSaveConflict, Task<CloudSaveResolution>>? ConflictResolver { get; set; }

    public CloudSaveManager(ILogger log, IStoreRepository repository, Storage storage)
    {
        _log = log;
        _repository = repository;
        _storage = storage;
    }

    public static bool SupportsCloudSaves(Game? game) =>
        !string.IsNullOrWhiteSpace(game?.Metadata?.CustomAttributes?.CloudSaveFolder?.Value);

    public CloudSaveSyncStatus GetSyncStatus(string appName) =>
        _syncStates.TryGetValue(appName, out var status) ? status : CloudSaveSyncStatus.Idle;

    /// <summary>
    /// Waits until no cloud save sync is running for <paramref name="appName"/>. Launch
    /// paths use this so a new session cannot start while a previous session is still
    /// uploading or downloading its saves.
    /// </summary>
    public async Task WaitForIdleAsync(string appName, CancellationToken cancellationToken = default)
    {
        var gate = _syncGates.GetOrAdd(appName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        gate.Release();
    }

    /// <summary>
    /// Downloads newer cloud saves before the game starts. Never throws; sync problems
    /// must not block launching.
    /// </summary>
    public async Task SyncBeforeLaunchAsync(Game game, CancellationToken cancellationToken = default)
    {
        if (!SupportsCloudSaves(game))
            return;

        await RunSyncAsync(game.AppName, cancellationToken, () => SyncBeforeLaunchCoreAsync(game, cancellationToken));
    }

    private async Task SyncBeforeLaunchCoreAsync(Game game, CancellationToken cancellationToken)
    {
        var savePath = await GetSavePathAsync(game);
        if (savePath is null)
        {
            _log.Warning("Cloud saves are supported for {AppName} but the save path could not be resolved", game.AppName);
            return;
        }

        SetStatus(game.AppName, CloudSaveSyncStatus.Checking);
        var remote = await GetLatestRemoteSaveAsync(game.AppName, cancellationToken);
        var local = ScanLocalSave(game, savePath);
        var state = CheckState(local, remote);
        _log.Information(
            "Cloud save check before launch for {AppName}: {State} (local files: {LocalCount}, local time: {LocalTime}, remote time: {RemoteTime})",
            game.AppName, state, local.FileCount, local.TimestampUtc, remote?.TimestampUtc);
        switch (state)
        {
            case CloudSaveState.RemoteNewer when !local.HasFiles:
                await DownloadSaveAsync(game, savePath, remote!, cleanDirectory: false, cancellationToken);
                return;
            case CloudSaveState.LocalNewer when remote is null:
                // Local saves exist and nothing is uploaded yet; there is no cloud copy
                // to conflict with. The upload happens after the session ends.
                return;
            case CloudSaveState.LocalNewer:
            case CloudSaveState.RemoteNewer:
                // Both sides exist but disagree; ask which copy survives before
                // a download can overwrite newer local progress.
                if (await ResolveConflictAsync(game, local, remote!, cancellationToken) == CloudSaveResolution.KeepCloud)
                    await DownloadSaveAsync(game, savePath, remote!, cleanDirectory: true, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// Uploads newer local saves after the game exits. When the cloud copy is newer the
    /// user decides; keeping the cloud copy downloads it so both sides match. Never throws.
    /// </summary>
    public async Task SyncAfterExitAsync(
        Game game,
        CancellationToken cancellationToken = default,
        Action? onFinished = null)
    {
        if (!SupportsCloudSaves(game))
        {
            onFinished?.Invoke();
            return;
        }

        await RunSyncAsync(
            game.AppName,
            cancellationToken,
            () => SyncAfterExitCoreAsync(game, cancellationToken),
            onFinished);
    }

    private async Task SyncAfterExitCoreAsync(Game game, CancellationToken cancellationToken)
    {
        var savePath = await GetSavePathAsync(game);
        if (savePath is null)
            return;

        SetStatus(game.AppName, CloudSaveSyncStatus.Checking);
        var local = ScanLocalSave(game, savePath);
        if (!local.HasFiles)
            return;

        var remote = await GetLatestRemoteSaveAsync(game.AppName, cancellationToken);
        var state = CheckState(local, remote);
        _log.Information(
            "Cloud save check after exit for {AppName}: {State} (local files: {LocalCount}, local time: {LocalTime}, remote time: {RemoteTime})",
            game.AppName, state, local.FileCount, local.TimestampUtc, remote?.TimestampUtc);
        switch (state)
        {
            case CloudSaveState.LocalNewer: // includes the no-remote-save case
                await UploadSaveAsync(game, savePath, local, cancellationToken);
                return;
            case CloudSaveState.RemoteNewer:
                // The cloud save changed behind this session (another machine, or a
                // crashed sync); overwriting it could lose that progress.
                var resolution = await ResolveConflictAsync(game, local, remote!, cancellationToken);
                if (resolution == CloudSaveResolution.KeepLocal)
                    await UploadSaveAsync(game, savePath, local, cancellationToken);
                else if (resolution == CloudSaveResolution.KeepCloud)
                    await DownloadSaveAsync(game, savePath, remote!, cleanDirectory: true, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// Runs one sync pass under the per-app gate and reports it idle afterwards. Sync
    /// failures are logged here and never reach the launch or exit path.
    /// </summary>
    private async Task RunSyncAsync(
        string appName,
        CancellationToken cancellationToken,
        Func<Task> body,
        Action? onFinished = null)
    {
        var gate = _syncGates.GetOrAdd(appName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await body();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning("Cloud save sync failed for {AppName}: {ErrorType}", appName, ex.GetType().Name);
        }
        finally
        {
            // Run the caller's completion hook before the status resets to Idle, so the
            // UI never briefly shows the game as running once the sync is over.
            try
            {
                onFinished?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "A cloud save completion hook for {AppName} failed", appName);
            }
            SetStatus(appName, CloudSaveSyncStatus.Idle);
            gate.Release();
        }
    }

    /// <summary>
    /// Resolves the CloudSaveFolder template of a game to an absolute path, or null when
    /// the game is not installed or the template contains variables that cannot be resolved.
    /// </summary>
    internal async Task<string?> GetSavePathAsync(Game game)
    {
        var template = game.Metadata?.CustomAttributes?.CloudSaveFolder?.Value;
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(game.LocalAppState?.InstallPath))
            return null;

        var accountId = (await _storage.GetUserData())?.AccountId;
        return ResolveSavePath(template, game.LocalAppState.InstallPath, accountId);
    }

    internal static string? ResolveSavePath(string template, string installPath, string? accountId)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["{installdir}"] = installPath,
            ["{epicid}"] = accountId,
            ["{appdata}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["{userdir}"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["{userprofile}"] = userProfile,
            ["{usersavedgames}"] = Path.Combine(userProfile, "Saved Games")
        };

        var segments = template.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => variables.TryGetValue(segment, out var value) ? value : segment)
            .ToList();
        if (segments.Count == 0 || segments.Any(string.IsNullOrWhiteSpace))
            return null;

        var combined = Path.Combine(segments.ToArray());
        // Reject templates with variables that survived substitution (legendary asks the
        // user for the correct path here; skipping sync is the safe non-interactive choice).
        if (combined.Contains('{') || combined.Contains('%'))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static LocalSaveInfo ScanLocalSave(Game game, string savePath) =>
        ScanLocalSave(savePath, IncludeFilters(game), ExcludeFilters(game));

    internal static LocalSaveInfo ScanLocalSave(
        string savePath,
        IReadOnlyList<string>? includeFilters,
        IReadOnlyList<string>? excludeFilters)
    {
        var files = SaveGamePacker.EnumerateSaveFiles(savePath, includeFilters, excludeFilters);
        if (files.Count == 0)
            return default;

        long size = 0;
        var latest = DateTimeOffset.MinValue;
        foreach (var relative in files)
        {
            var info = new FileInfo(Path.Combine(savePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists)
                continue;
            size += info.Length;
            if (info.LastWriteTimeUtc > latest)
                latest = info.LastWriteTimeUtc;
        }

        return new LocalSaveInfo(latest > DateTimeOffset.MinValue ? latest : null, size, files.Count);
    }

    internal static CloudSaveState CheckState(LocalSaveInfo local, RemoteSaveInfo? remote)
    {
        if (!local.HasFiles && remote is null)
            return CloudSaveState.NoSave;
        if (remote is null)
            return CloudSaveState.LocalNewer;
        if (!local.HasFiles)
            return CloudSaveState.RemoteNewer;
        // Files were seen but vanished before they could be stat'ed; treat the remote as
        // newer so both sync directions fall back to asking the user instead of picking.
        if (local.TimestampUtc is null)
            return CloudSaveState.RemoteNewer;

        var difference = (local.TimestampUtc.Value - remote.TimestampUtc).Duration();
        if (difference < SameAgeTolerance)
            return CloudSaveState.SameAge;
        return local.TimestampUtc.Value > remote.TimestampUtc
            ? CloudSaveState.LocalNewer
            : CloudSaveState.RemoteNewer;
    }

    internal static IReadOnlyList<string>? IncludeFilters(Game game) =>
        SaveGamePacker.ParseFilters(game.Metadata?.CustomAttributes?.CloudIncludeList?.Value);

    internal static IReadOnlyList<string>? ExcludeFilters(Game game) =>
        SaveGamePacker.ParseFilters(game.Metadata?.CustomAttributes?.CloudExcludeList?.Value);

    private async Task<RemoteSaveInfo?> GetLatestRemoteSaveAsync(string appName, CancellationToken cancellationToken)
    {
        var files = await _repository.GetCloudSaveFilesAsync(appName, manifestsOnly: true, cancellationToken);
        // A failed listing must not look like "no remote saves": at exit that would upload
        // blindly over an unseen newer cloud manifest. Throw so the caller skips the sync.
        return files is null
            ? throw new InvalidOperationException("Listing cloud saves failed.")
            : GetLatestRemoteSave(appName, files);
    }

    internal static RemoteSaveInfo? GetLatestRemoteSave(
        string appName,
        IReadOnlyDictionary<string, CloudSaveFileEntry>? files)
    {
        if (files is null)
            return null;

        RemoteSaveInfo? latest = null;
        foreach (var (path, _) in files)
        {
            if (!TryParseManifestPath(appName, path, out var manifestName, out var timestampUtc))
                continue;
            if (latest is null || timestampUtc > latest.TimestampUtc)
                latest = new RemoteSaveInfo(appName, path, manifestName, timestampUtc);
        }

        return latest;
    }

    internal static bool TryParseManifestPath(
        string appName,
        string path,
        out string manifestName,
        out DateTimeOffset timestampUtc)
    {
        manifestName = string.Empty;
        timestampUtc = default;
        var segments = path.Split('/');
        var manifestsIndex = Array.FindIndex(segments, segment => segment == "manifests");
        if (manifestsIndex < 1 || manifestsIndex != segments.Length - 2 ||
            !string.Equals(segments[manifestsIndex - 1], appName, StringComparison.Ordinal))
            return false;

        var fileName = segments[^1];
        if (!fileName.EndsWith(".manifest", StringComparison.Ordinal))
            return false;

        var stamp = fileName[..^".manifest".Length];
        if (!DateTimeOffset.TryParseExact(
                stamp,
                SaveGamePacker.ManifestTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out timestampUtc))
            return false;

        manifestName = fileName;
        return true;
    }

    private async Task<CloudSaveResolution> ResolveConflictAsync(
        Game game,
        LocalSaveInfo local,
        RemoteSaveInfo remote,
        CancellationToken cancellationToken)
    {
        var resolver = ConflictResolver;
        if (resolver is null)
            return CloudSaveResolution.Skip;

        try
        {
            var remoteSize = await GetRemoteSaveSizeAsync(remote, cancellationToken);
            var conflict = new CloudSaveConflict(
                game.AppName,
                local.TimestampUtc ?? DateTimeOffset.MinValue,
                remote.TimestampUtc,
                local.SizeBytes,
                remoteSize ?? -1);
            return await resolver(conflict);
        }
        catch (Exception ex)
        {
            _log.Warning("Cloud save conflict resolution failed for {AppName}: {ErrorType}", game.AppName, ex.GetType().Name);
            return CloudSaveResolution.Skip;
        }
    }

    private async Task<long?> GetRemoteSaveSizeAsync(RemoteSaveInfo remote, CancellationToken cancellationToken)
    {
        var manifestBytes = await DownloadManifestBytesAsync(remote, cancellationToken);
        if (manifestBytes is null)
            return null;
        var manifest = Manifest.ReadAll(manifestBytes);
        return manifest.FileManifestList.Elements.Sum(file => file.FileSize);
    }

    private async Task<byte[]?> DownloadManifestBytesAsync(RemoteSaveInfo remote, CancellationToken cancellationToken)
    {
        var files = await _repository.GetCloudSaveFilesAsync(remote.AppName, manifestsOnly: true, cancellationToken);
        if (files is null || !files.TryGetValue(remote.Path, out var entry) || string.IsNullOrEmpty(entry.ReadLink))
            return null;
        return await _repository.DownloadCloudSaveFileAsync(entry.ReadLink, cancellationToken);
    }

    private async Task UploadSaveAsync(
        Game game,
        string savePath,
        LocalSaveInfo local,
        CancellationToken cancellationToken)
    {
        var accountId = (await _storage.GetUserData())?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId))
            throw new InvalidOperationException("Cannot upload saves without an account id.");

        SetStatus(game.AppName, CloudSaveSyncStatus.Uploading);
        // Naming the manifest after the local save timestamp makes the next check see
        // both sides as the same age, mirroring legendary's upload_save.
        var files = SaveGamePacker.Package(
            savePath,
            game.AppName,
            accountId,
            game.Metadata.CustomAttributes.CloudSaveFolder!.Value!,
            IncludeFilters(game),
            ExcludeFilters(game),
            local.TimestampUtc ?? DateTimeOffset.UtcNow);
        if (files.Count == 0)
        {
            _log.Information("No save files to upload for {AppName}", game.AppName);
            return;
        }

        _log.Information("Uploading {FileCount} cloud save files for {AppName}", files.Count, game.AppName);
        var listing = await _repository.CreateCloudSaveFilesAsync(game.AppName, files.Keys.ToList(), cancellationToken);
        if (listing is null)
            throw new InvalidOperationException("Reserving cloud save files failed.");

        foreach (var (path, bytes) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!listing.TryGetValue(path, out var entry) || string.IsNullOrEmpty(entry.WriteLink))
                throw new InvalidDataException("The cloud save reservation is missing a write link.");
            if (!await _repository.UploadCloudSaveFileAsync(entry.WriteLink, bytes, cancellationToken))
                throw new InvalidOperationException("A cloud save file upload failed.");
        }

        _log.Information("Finished uploading cloud saves for {AppName}", game.AppName);
    }

    private async Task DownloadSaveAsync(
        Game game,
        string savePath,
        RemoteSaveInfo remote,
        bool cleanDirectory,
        CancellationToken cancellationToken)
    {
        SetStatus(game.AppName, CloudSaveSyncStatus.Downloading);
        _log.Information("Downloading cloud save {ManifestName} for {AppName}", remote.ManifestName, game.AppName);
        var (manifest, chunks) = await FetchRemoteSaveAsync(game.AppName, remote, cancellationToken);

        // Write the whole new copy into a sibling staging directory first. The live save is
        // only touched after every file exists, so a failed or interrupted download leaves
        // the user's current save intact.
        var staging = CreateSiblingDirectory(savePath, "staging");
        try
        {
            var relativePaths = WriteStagedSave(staging, manifest, chunks, remote.TimestampUtc, cancellationToken);
            SwapStagedSave(game, savePath, staging, relativePaths, cleanDirectory);
        }
        finally
        {
            DeleteDirectory(staging);
        }

        _log.Information("Finished downloading cloud saves for {AppName}", game.AppName);
    }

    private async Task<(Manifest Manifest, Dictionary<BigInteger, byte[]> Chunks)> FetchRemoteSaveAsync(
        string appName,
        RemoteSaveInfo remote,
        CancellationToken cancellationToken)
    {
        var files = await _repository.GetCloudSaveFilesAsync(appName, manifestsOnly: false, cancellationToken);
        if (files is null)
            throw new InvalidOperationException("Listing cloud save files failed.");
        if (!files.TryGetValue(remote.Path, out var manifestEntry) || string.IsNullOrEmpty(manifestEntry.ReadLink))
            throw new InvalidDataException("The newest cloud save manifest is missing a read link.");

        var manifestBytes = await _repository.DownloadCloudSaveFileAsync(manifestEntry.ReadLink, cancellationToken);
        if (manifestBytes is not { Length: > 0 })
            throw new InvalidDataException("The newest cloud save manifest could not be downloaded.");

        var manifest = Manifest.ReadAll(manifestBytes);
        var prefixLength = remote.Path.LastIndexOf("manifests/", StringComparison.Ordinal);
        if (prefixLength < 0)
            throw new InvalidDataException("The cloud save manifest path is malformed.");
        var prefix = remote.Path[..prefixLength];

        var chunks = new Dictionary<BigInteger, byte[]>();
        foreach (var chunkInfo in manifest.CDL.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(prefix + chunkInfo.Path, out var chunkEntry) || string.IsNullOrEmpty(chunkEntry.ReadLink))
                throw new InvalidDataException("A cloud save chunk is missing from the file listing.");
            var chunkBytes = await _repository.DownloadCloudSaveFileAsync(chunkEntry.ReadLink, cancellationToken);
            if (chunkBytes is null)
                throw new InvalidDataException("A cloud save chunk could not be downloaded.");
            var chunk = Chunk.ReadBuffer(chunkBytes);
            chunks[chunk.GuidNum] = chunk.Data;
        }

        return (manifest, chunks);
    }

    private static List<string> WriteStagedSave(
        string staging,
        Manifest manifest,
        Dictionary<BigInteger, byte[]> chunks,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken)
    {
        var relativePaths = new List<string>(manifest.FileManifestList.Elements.Count);
        foreach (var file in manifest.FileManifestList.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = InstallPathPolicy.ResolveFile(staging, file.Filename);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var part in file.ChunkParts)
                {
                    if (!chunks.TryGetValue(part.GuidNum, out var data))
                        throw new InvalidDataException("A cloud save file references a missing chunk.");
                    output.Write(data, part.Offset, part.Size);
                }
            }

            // Preserve the save timestamp so the next state check compares correctly.
            File.SetLastWriteTimeUtc(destination, timestampUtc.UtcDateTime);
            relativePaths.Add(file.Filename);
        }

        return relativePaths;
    }

    /// <summary>
    /// Replaces the live save files with the staged copy and keeps the previous files in a
    /// backup until every move succeeds. A failure rolls the backup back into place.
    /// </summary>
    private void SwapStagedSave(
        Game game,
        string savePath,
        string staging,
        IReadOnlyList<string> relativePaths,
        bool cleanDirectory)
    {
        var destinations = relativePaths
            .Select(relative => InstallPathPolicy.ResolveFile(savePath, relative))
            .ToList();
        var backup = CreateSiblingDirectory(savePath, "backup");
        var installed = new List<string>(destinations.Count);

        try
        {
            // Move the files the new copy replaces aside first, so a failed swap can be
            // fully undone.
            foreach (var relative in relativePaths)
            {
                var destination = InstallPathPolicy.ResolveFile(savePath, relative);
                if (!File.Exists(destination)) continue;
                var backupFile = InstallPathPolicy.ResolveFile(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                File.Move(destination, backupFile, overwrite: true);
            }

            foreach (var relative in relativePaths)
            {
                var destination = InstallPathPolicy.ResolveFile(savePath, relative);
                var staged = InstallPathPolicy.ResolveFile(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(staged, destination, overwrite: true);
                installed.Add(destination);
            }
        }
        catch
        {
            foreach (var destination in installed) DeleteSaveFile(game, destination);
            if (RestoreBackup(game, savePath, backup)) DeleteDirectory(backup);
            throw;
        }

        // The new copy is complete; remove files the manifest no longer contains. Failure
        // here only leaves a stale file behind and must not undo the successful swap.
        if (cleanDirectory)
            DeleteStaleSaveFiles(game, savePath, destinations);
        DeleteDirectory(backup);
    }

    private bool RestoreBackup(Game game, string savePath, string backup)
    {
        var restored = true;
        foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backup, file);
            try
            {
                var destination = InstallPathPolicy.ResolveFile(savePath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                // Keep the backup directory: it now holds the only copy of this file.
                restored = false;
                _log.Error(ex,
                    "Failed to restore a backed-up save file for {AppName}; it remains in {BackupPath}",
                    game.AppName, backup);
            }
        }
        return restored;
    }

    private void DeleteStaleSaveFiles(Game game, string savePath, IReadOnlyCollection<string> keep)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var retained = keep.ToHashSet(comparer);
        foreach (var relative in SaveGamePacker.EnumerateSaveFiles(savePath, IncludeFilters(game), ExcludeFilters(game)))
        {
            var destination = InstallPathPolicy.ResolveFile(savePath, relative);
            if (!retained.Contains(destination)) DeleteSaveFile(game, destination);
        }
    }

    private void DeleteSaveFile(Game game, string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning("Failed to delete save file for {AppName}: {ErrorType}", game.AppName, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Creates a working directory next to the save directory, so moving files in and out
    /// stays on the same volume and never lands inside the game's save scan.
    /// </summary>
    private static string CreateSiblingDirectory(string savePath, string purpose)
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(savePath)));
        root = string.IsNullOrEmpty(root) ? Path.GetTempPath() : root;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(savePath));
        var directory = Path.Combine(root, $".{name}.crimson-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Warning("Failed to remove a temporary cloud save directory: {ErrorType}", ex.GetType().Name);
        }
    }

    private void SetStatus(string appName, CloudSaveSyncStatus status)
    {
        if (status == CloudSaveSyncStatus.Idle)
        {
            if (!_syncStates.TryRemove(appName, out _))
                return;
        }
        else if (_syncStates.TryGetValue(appName, out var current) && current == status)
        {
            return;
        }
        else
        {
            _syncStates[appName] = status;
        }

        try
        {
            SyncStatusChanged?.Invoke(appName, status);
        }
        catch (Exception ex)
        {
            // A misbehaving observer must not break the sync flow or leak the per-app gate.
            _log.Warning(ex, "A cloud save status observer for {AppName} failed", appName);
        }
    }
}
