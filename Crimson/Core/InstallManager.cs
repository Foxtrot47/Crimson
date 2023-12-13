using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public class InstallManager
{
    public event Action<InstallItem> InstallationStatusChanged;
    public event Action<InstallItem> InstallProgressUpdate;
    public InstallItem CurrentInstall;
    private readonly Queue<InstallItem> _installQueue;
    private readonly List<InstallItem> _installHistory = new();

    private readonly ConcurrentQueue<DownloadTask> _downloadQueue = new();
    private readonly ConcurrentQueue<IoTask> _ioQueue = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();

    private readonly ConcurrentDictionary<string, List<FileManifest>> _chunkToFileManifestsDictionary =
        new();

    private readonly ConcurrentDictionary<string, int> _chunkPartReferences = new();

    private bool InstallFinalizing = false;

    private readonly object _finalizeInstallLock = new();
    private readonly object _installItemLock = new();

    private ILogger _log;
    private readonly LibraryManager _libraryManager;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;

    private readonly int _numberOfThreads;
    private const int _progressUpdateIntervalInMS = 500;

    private Stopwatch _installStopWatch = new();

    public InstallManager(ILogger log, LibraryManager libraryManager, IStoreRepository repository, Storage storage)
    {
        _log = log;
        _libraryManager = libraryManager;
        _installQueue = new Queue<InstallItem>();
        CurrentInstall = null;
        _repository = repository;
        _storage = storage;

        _numberOfThreads = Environment.ProcessorCount;
    }

    public void AddToQueue(InstallItem item)
    {
        if (item == null)
            return;

        // check if the game is already in the queue
        if (_installQueue.Contains(item, new InstallItemComparer()))
        {
            _log.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
            return;
        }

        // Check if the game we are trying to install exists in the library
        var gameData = _libraryManager.GetGameInfo(item.AppName);
        if (gameData == null)
        {
            _log.Warning("AddToQueue: Game {Name} not found in library", item.AppName);
            return;
        }

        if (item.Action != ActionType.Install && gameData.InstallStatus == InstallState.NotInstalled)
        {
            _log.Warning($"AddToQueue: {item.AppName} is not installed, cannot {item.Action.ToString()}");
            return;
        }

        if (item.Action != ActionType.Repair && gameData.InstallStatus == InstallState.Broken)
        {
            _log.Warning($"AddToQueue: {item.AppName} is broken, forcing repair");
            item.Action = ActionType.Repair;
        }

        if (gameData.IsDlc())
        {
            _log.Warning($"AddToQueue: {item.AppName} is a DLC. DLC Handling is disabled right now");
            return;
        }

        _log.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        _installQueue.Enqueue(item);
        if (CurrentInstall == null)
            ProcessNext();
    }

    private async void ProcessNext()
    {
        try
        {
            if (CurrentInstall != null || _installQueue.Count <= 0) return;

            CurrentInstall = _installQueue.Dequeue();
            if (CurrentInstall == null) return;
            _log.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            var manifestData = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
                gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);

            gameData.BaseUrls = manifestData.BaseUrls;
            _storage.SaveMetaData(gameData);

            _log.Information("ProcessNext: Parsing game manifest");
            var data = Manifest.ReadAll(manifestData.ManifestBytes);

            // TODO Handle stats if game is installed

            // create CurrentInstall.folder if it doesn't exist
            if (!Directory.Exists(CurrentInstall.Location))
            {
                Directory.CreateDirectory(CurrentInstall.Location);
                _log.Debug("Folder created at: {location}", CurrentInstall.Location);
            }

            if (!HasFolderWritePermissions(CurrentInstall.Location))
            {
                _log.Error("ProcessNext: No write permissions to {Location}", CurrentInstall.Location);
                CurrentInstall.Status = ActionStatus.Failed;
                InstallationStatusChanged?.Invoke(CurrentInstall);
                CurrentInstall = null;
                ProcessNext();
            }

            var chunkDownloadList = new List<ChunkInfo>();

            foreach (var fileManifest in data.FileManifestList.Elements)
            {
                foreach (var chunkPart in fileManifest.ChunkParts)
                {
                    if (_chunkToFileManifestsDictionary.TryGetValue(chunkPart.GuidStr, out var fileManifests))
                    {
                        fileManifests.Add(fileManifest);
                        _chunkToFileManifestsDictionary[chunkPart.GuidStr] = fileManifests;
                    }
                    else
                    {
                        _ = _chunkToFileManifestsDictionary.TryAdd(chunkPart.GuidStr,
                            new List<FileManifest>() { fileManifest });
                    }

                    if (chunkDownloadList.FirstOrDefault(chunk => chunk.GuidStr == chunkPart.GuidStr) != null) continue;

                    var chunkInfo = data.CDL.GetChunkByGuid(chunkPart.GuidStr);
                    var newTask = new DownloadTask()
                    {
                        Url = manifestData.BaseUrls.FirstOrDefault() + "/" + chunkInfo.Path,
                        TempPath = Path.Combine(CurrentInstall.Location, ".temp", (chunkInfo.GuidStr + ".chunk")),
                        Guid = chunkInfo.GuidStr,
                        ChunkInfo = chunkInfo
                    };
                    _log.Debug("ProcessNext: Adding new download task {@task}", newTask);
                    chunkDownloadList.Add(chunkInfo);
                    _downloadQueue.Enqueue(newTask);

                    CurrentInstall.TotalDownloadSizeMb += chunkInfo.FileSize / 1000000.0;
                }
                CurrentInstall.TotalWriteSizeMb += fileManifest.FileSize / 1000000.0;
            }

            CurrentInstall!.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            _installStopWatch.Start();

            for (var i = 0; i < _numberOfThreads; i++)
            {
                Thread thread1 = new(async () => await ProcessDownloadQueue());
                Thread thread2 = new(async () => await ProcessIoQueue());
                thread1.Start();
                thread2.Start();
            }
        }
        catch (Exception ex)
        {
            _log.Error("ProcessNext: {Exception}", ex);
            await _cancellationTokenSource.CancelAsync();
            if (CurrentInstall != null)
            {
                CurrentInstall.Status = ActionStatus.Failed;
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }

            CurrentInstall = null;
            ProcessNext();
        }
    }

    public InstallItem GameGameInQueue(string gameName)
    {
        InstallItem item;
        if (CurrentInstall != null && CurrentInstall.AppName == gameName)
            item = CurrentInstall;
        else
            item = _installQueue.FirstOrDefault(r => r.AppName == gameName);
        return item;
    }

    public void CancelInstall(string gameName)
    {
    }

    public List<string> GetQueueItemNames()
    {
        return _installQueue.Select(item => item.AppName).ToList();
    }

    public List<string> GetHistoryItemsNames()
    {
        return _installHistory.Select(item => item.AppName).ToList();
    }

    private bool HasFolderWritePermissions(string folderPath)
    {
        try
        {
            // Create a DirectoryInfo object representing the specified directory.
            var directoryInfo = new DirectoryInfo(folderPath);

            // Get the access control list for the folder
            var directorySecurity = directoryInfo.GetAccessControl();

            // Get the access rules for the current user and their groups
            var currentUser = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(currentUser);

            var hasWritePermissions = directorySecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(rule =>
                    (currentUser.User.Equals(rule.IdentityReference) ||
                     principal.IsInRole((SecurityIdentifier)rule.IdentityReference)) &&
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write);

            return hasWritePermissions;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"An error occurred: {ex.Message}");
            return false;
        }
    }

    public void StopProcessing()
    {
        _cancellationTokenSource.Cancel();
    }

    private async Task ProcessDownloadQueue()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            if (_downloadQueue.TryDequeue(out var downloadTask))
            {
                try
                {
                    _log.Debug("ProcessDownloadQueue: Downloading chunk with guid{guid} from {url} to {path}", downloadTask.Guid, downloadTask.Url, downloadTask.TempPath);
                    await _repository.DownloadFileAsync(downloadTask.Url, downloadTask.TempPath);

                    // Use a lock to synchronize writes to current install thread safe
                    lock (_installItemLock)
                    {
                        CurrentInstall.DownloadedSize += downloadTask.ChunkInfo.FileSize / 1000000.0;

                        CurrentInstall.DownloadSpeedRaw = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
                            ? Math.Round(CurrentInstall.DownloadedSize / _installStopWatch.Elapsed.TotalSeconds, 2)
                            : 0;
                        // Limit firing progress update events
                        if (_installStopWatch.Elapsed.TotalSeconds % _progressUpdateIntervalInMS == 0)
                        {
                            InstallProgressUpdate?.Invoke(CurrentInstall);
                        }

                    }

                    // get file manifest from dictionary
                    var fileManifests = _chunkToFileManifestsDictionary[downloadTask.Guid];
                    foreach (var fileManifest in fileManifests)
                    {
                        foreach (var part in fileManifest.ChunkParts)
                        {
                            if (part.GuidStr != downloadTask.Guid) continue;

                            _log.Debug("ProcessDownloadQueue: New file reference for chunk {guid} filename:{filename}", downloadTask.Guid, fileManifest.Filename);
                            // keep track of files count to which the parts of chunk must be copied to
                            _chunkPartReferences.AddOrUpdate(
                                part.GuidStr,
                                1, // Add with a count of 1 if not present
                                (key, oldValue) => oldValue + 1 // Update: increment the count
                            );

                            var task = new IoTask()
                            {
                                SourceFilePath = downloadTask.TempPath,
                                DestinationFilePath = Path.Combine(CurrentInstall.Location, fileManifest.Filename),
                                TaskType = IoTaskType.Copy,
                                Size = part.Size,
                                Offset = part.Offset,
                                FileOffset = part.FileOffset,
                                GuidStr = part.GuidStr
                            };
                            _log.Debug("ProcessDownloadQueue: Adding ioTask {task}", task);
                            _ioQueue.Enqueue(task);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("ProcessDownloadQueue: Exception: {ex}", ex);
                    await _cancellationTokenSource.CancelAsync();
                    if (CurrentInstall != null)
                    {
                        CurrentInstall.Status = ActionStatus.Failed;
                        InstallationStatusChanged?.Invoke(CurrentInstall);
                        CurrentInstall = null;
                    }
                }
            }
            else
            {
                await Task.Delay(500);
            }
        }
    }

    private async Task ProcessIoQueue()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            if (_ioQueue.TryDequeue(out var ioTask))
            {
                try
                {
                    switch (ioTask.TaskType)
                    {
                        case IoTaskType.Copy:
                            // Ensure there is a lock object for each destination file
                            var fileLock =
                                _fileLocksConcurrentDictionary.GetOrAdd(ioTask.DestinationFilePath, new object());

                            var compressedChunkData = await File.ReadAllBytesAsync(ioTask.SourceFilePath);
                            var chunk = Chunk.ReadBuffer(compressedChunkData);
                            _log.Debug("ProcessIoQueue: Reading chunk buffers from {source} finished", ioTask.SourceFilePath);

                            var directoryPath = Path.GetDirectoryName(ioTask.DestinationFilePath);
                            if (!string.IsNullOrEmpty(directoryPath))
                            {
                                Directory.CreateDirectory(directoryPath);
                            }

                            lock (fileLock)
                            {
                                using var fileStream = new FileStream(ioTask.DestinationFilePath, FileMode.OpenOrCreate,
                                    FileAccess.Write, FileShare.None);

                                _log.Debug("ProcessIoQueue: Seeking {seek}bytes on file {destination}", ioTask.FileOffset, ioTask.DestinationFilePath);
                                fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);

                                // Since chunk offset is a long we cannot use it directly in File stream write or read
                                // Use a memory stream to seek to the chunk offset
                                using var memoryStream = new MemoryStream(chunk.Data);
                                memoryStream.Seek(ioTask.Offset, SeekOrigin.Begin);

                                var remainingBytesToWrite = ioTask.Size;
                                // Buffer size is irrelevant as write is continuous
                                const int bufferSize = 4096;
                                var buffer = new byte[bufferSize];

                                _log.Debug("ProcessIoQueue: Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);

                                while (remainingBytesToWrite > 0)
                                {
                                    var bytesToRead = (int)Math.Min(bufferSize, remainingBytesToWrite);
                                    var bytesRead = memoryStream.Read(buffer, 0, bytesToRead);
                                    fileStream.Write(buffer, 0, bytesRead);

                                    remainingBytesToWrite -= bytesRead;
                                }

                                fileStream.Flush();
                                _log.Debug("ProcessIoQueue: Finished Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);
                            }

                            lock (_installItemLock)
                            {
                                CurrentInstall.WrittenSize += ioTask.Size / 1000000.0;
                                CurrentInstall.WriteSpeed = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
                                    ? Math.Round(CurrentInstall.WrittenSize / _installStopWatch.Elapsed.TotalSeconds, 2)
                                    : 0;
                                CurrentInstall.ProgressPercentage = Convert.ToInt32((CurrentInstall.WrittenSize / CurrentInstall.TotalWriteSizeMb) * 100);

                                // Limit firing progress update events
                                if (_installStopWatch.Elapsed.TotalSeconds % _progressUpdateIntervalInMS == 0)
                                {
                                    InstallProgressUpdate?.Invoke(CurrentInstall);
                                }
                            }

                            // Check for references to the chunk and decrement by one
                            int newCount = _chunkPartReferences.AddOrUpdate(
                                ioTask.GuidStr,
                                (key) => 0, // Not expected to be called as the key should exist
                                (key, oldValue) =>
                                {
                                    _log.Debug("ProcessIoQueue: decrementing reference count of {guid} by 1. Current value:{oldValue}", ioTask.GuidStr, oldValue);
                                    return oldValue - 1;
                                }
                            );

                            // Check if the updated count is 0 or less
                            if (newCount <= 0)
                            {
                                // Attempt to remove the item from the dictionary
                                if (_chunkPartReferences.TryRemove(ioTask.GuidStr, out _))
                                {
                                    _log.Debug("ProcessIoQueue: Deleting chunk file {file}", ioTask.SourceFilePath);
                                    // Delete the file if successfully removed
                                    File.Delete(ioTask.SourceFilePath);
                                }
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("ProcessIoQueue: IO task failed with exception {ex}", ex);
                    await _cancellationTokenSource.CancelAsync();
                    if (CurrentInstall != null)
                    {
                        CurrentInstall.Status = ActionStatus.Failed;
                        InstallationStatusChanged?.Invoke(CurrentInstall);
                        CurrentInstall = null;
                    }

                    ProcessNext();
                }
            }
            else
            {
                await Task.Delay(500);
            }

            if (_chunkPartReferences.Count <= 0 && _downloadQueue.IsEmpty && CurrentInstall != null)
            {
                _log.Information("ProcessIoQueue: Both queues are empty. Starting finalize stage");
                _ = UpdateInstalledGameStatus();
            }
        }
    }

    public static string CalculateSHA1(byte[] data)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(data);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    }

    /// <summary>
    /// Creates or updates installed games list after install completed
    /// </summary>
    /// <exception cref="Exception"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private async Task UpdateInstalledGameStatus()
    {
        try
        {
            // Ensure we only fire this function once.
            // Since we cannot use lock over entire function , lock access to a variable and check it allow access
            lock (_finalizeInstallLock)
            {
                if (InstallFinalizing) return;
                InstallFinalizing = true;
            }

            if (CurrentInstall == null) return;

            // Intentional delay to wait till all files are written
            await Task.Delay(2000);

            // Stop all queues before doing anything
            await _cancellationTokenSource.CancelAsync();
            _installStopWatch.Reset();

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            if (gameData == null)
            {
                _log.Error("UpdateInstalledGameStatus: Found no game data for app name: {AppName}",
                    CurrentInstall.AppName);
                throw new Exception("Invalid game data");
            }

            if (!_storage.InstalledGamesDictionary.TryGetValue(CurrentInstall.AppName, out InstalledGame installedGame))
            {
                installedGame = new InstalledGame();
            }

            var manifestDataBytes = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
                gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);

            await File.WriteAllBytesAsync(CurrentInstall.Location + "/.temp/manifest", manifestDataBytes.ManifestBytes);

            var manifestData = Manifest.ReadAll(manifestDataBytes.ManifestBytes);
            var canRunOffLine = gameData.Metadata.CustomAttributes.CanRunOffline.Value == "true";
            var requireOwnerShipToken = gameData.Metadata.CustomAttributes?.OwnershipToken?.Value == "true";

            if (installedGame?.AppName == null)
            {
                installedGame = new InstalledGame()
                {
                    AppName = CurrentInstall.AppName,
                    IsDlc = gameData.IsDlc()
                };
            }

            installedGame.BaseUrls = gameData.BaseUrls;
            installedGame.CanRunOffline = canRunOffLine;
            installedGame.Executable = manifestData.ManifestMeta.LaunchExe;
            installedGame.InstallPath = CurrentInstall.Location;
            installedGame.LaunchParameters = manifestData.ManifestMeta.LaunchCommand;
            installedGame.RequiresOt = requireOwnerShipToken;
            installedGame.Version = manifestData.ManifestMeta.BuildVersion;
            installedGame.Title = gameData.AppTitle;

            if (manifestData.ManifestMeta.UninstallActionPath != null)
            {
                installedGame.Uninstaller = new Dictionary<string, string>
                {
                    { manifestData.ManifestMeta.UninstallActionPath, manifestData.ManifestMeta.UninstallActionArgs }
                };
            }

            _log.Information("UpdateInstalledGameStatus: Adding new entry installed games list {@entry}",
                installedGame);

            _storage.SaveInstalledGamesList(installedGame);

            gameData.InstallStatus = CurrentInstall.Action switch
            {
                ActionType.Install or ActionType.Update or ActionType.Move or ActionType.Repair => InstallState
                    .Installed,
                ActionType.Uninstall => InstallState.NotInstalled,
                _ => throw new ArgumentOutOfRangeException(),
            };
            _libraryManager.UpdateGameInfo(gameData);
            InstallFinalizing = false;

            CurrentInstall.Status = ActionStatus.Success;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            CurrentInstall = null;
            ProcessNext();
        }
        catch (Exception ex)
        {
            _log.Fatal("UpdateInstalledGameStatus: Exception {ex}", ex);

            CurrentInstall.Status = ActionStatus.Failed;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            CurrentInstall = null;
            ProcessNext();
        }
    }
}

internal class InstallItemComparer : IEqualityComparer<InstallItem>
{
    public bool Equals(InstallItem x, InstallItem y)
    {
        return y != null && x != null && x.AppName == y.AppName;
    }

    public int GetHashCode(InstallItem obj)
    {
        return obj.AppName.GetHashCode();
    }
}

public class DownloadTask
{
    public string Url { get; set; }

    public string Guid { get; set; }

    public string TempPath { get; set; }

    public ChunkInfo ChunkInfo { get; set; }
}

public class IoTask
{
    public string SourceFilePath { get; set; }
    public string DestinationFilePath { get; set; }
    public long Size { get; set; }
    public long Offset { get; set; }
    public long FileOffset { get; set; }
    public IoTaskType TaskType { get; set; }
    public string GuidStr { get; set; }
}

public enum IoTaskType
{
    Copy,
    Create,
    Delete,
    Read
}
