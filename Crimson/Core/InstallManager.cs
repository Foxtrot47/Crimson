﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
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
    private ConcurrentQueue<IoTask> _ioQueue = new();
    private CancellationTokenSource _cancellationTokenSource = new();

    private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();

    private readonly ConcurrentDictionary<string, List<FileManifest>> _chunkToFileManifestsDictionary =
        new();

    private ConcurrentDictionary<string, int> _chunkPartReferences = new();

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
    private DateTime _lastUpdateTime = DateTime.MinValue;

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

        if (item.Action != ActionType.Repair && item.Action != ActionType.Uninstall && gameData.InstallStatus == InstallState.Broken)
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


            if (!HasFolderWritePermissions(CurrentInstall.Location))
            {
                _log.Error("ProcessNext: No write permissions to {Location}", CurrentInstall.Location);
                CurrentInstall.Status = ActionStatus.Failed;
                InstallationStatusChanged?.Invoke(CurrentInstall);
                CurrentInstall = null;
                ProcessNext();
            }

            if (CurrentInstall.Action == ActionType.Install)
            {
                // create CurrentInstall.folder if it doesn't exist
                if (!Directory.Exists(CurrentInstall.Location))
                {
                    Directory.CreateDirectory(CurrentInstall.Location);
                    _log.Debug("Folder created at: {location}", CurrentInstall.Location);
                }

                GetChunksToDownload(manifestData, data);
            }
            else if (CurrentInstall.Action == ActionType.Uninstall)
            {
                foreach (var fileManifest in data.FileManifestList.Elements)
                {
                    CurrentInstall.TotalWriteSizeMb += fileManifest.FileSize / 1000000.0;

                    var task = new IoTask()
                    {
                        DestinationFilePath = Path.Combine(CurrentInstall.Location, fileManifest.Filename),
                        TaskType = IoTaskType.Delete,
                        Size = fileManifest.FileSize,
                    };
                    _log.Debug("ProcessNext: Adding ioTask: {task}", task);
                    _ioQueue.Enqueue(task);
                }
            }

            CurrentInstall!.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            // Reset cancellation token
            _cancellationTokenSource = new CancellationTokenSource();
            _installStopWatch.Start();

            for (var i = 0; i < _numberOfThreads; i++)
            {
                Thread thread1 = new(async () => await ProcessDownloadQueue());
                Thread thread2 = new(() => new IoWorker(_log, ref _cancellationTokenSource, ref _ioQueue, this, ref _chunkPartReferences));
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
                        if ((DateTime.Now - _lastUpdateTime).TotalMilliseconds >= _progressUpdateIntervalInMS)
                        {
                            _lastUpdateTime = DateTime.Now;
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
                    await FailInstall();
                }
            }
            else
            {
                await Task.Delay(500);
            }
        }
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
            var installedGamesDictionary = _storage.InstalledGamesDictionary;
            installedGamesDictionary ??= [];

            if (installedGamesDictionary.Count > 0 && CurrentInstall.Action == ActionType.Uninstall)
            {
                installedGamesDictionary.Remove(CurrentInstall.AppName);
                _log.Information("UpdateInstalledGameStatus: Removing entry: {appName} from installed games list", CurrentInstall.AppName);
            }
            else
            {
                if (!installedGamesDictionary.TryGetValue(CurrentInstall.AppName, out var installedGame))
                {
                    installedGame = new InstalledGame();
                }

                var manifestDataBytes = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
                    gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);
                var manifestData = Manifest.ReadAll(manifestDataBytes.ManifestBytes);

                // Verify all the files
                CurrentInstall.Action = ActionType.Verify;
                InstallationStatusChanged?.Invoke(CurrentInstall);
                var invalidFilesList = await VerifyFiles(CurrentInstall.Location, manifestData.FileManifestList.Elements);

                if (invalidFilesList.Count > 0)
                {
                    // We will handle this later
                    // For now fail install
                    throw new Exception("UpdateInstalledGameStatus: Verification failed");
                }
                _log.Information("UpdateInstalledGameStatus: Verification successful for {appName}", CurrentInstall.AppName);

                await File.WriteAllBytesAsync(CurrentInstall.Location + "/.temp/manifest", manifestDataBytes.ManifestBytes);

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

                installedGamesDictionary.Add(gameData.AppName, installedGame);
            }

            _storage.UpdateInstalledGames(installedGamesDictionary);

            gameData.InstallStatus = CurrentInstall.Action switch
            {
                ActionType.Install or ActionType.Update or ActionType.Move or ActionType.Repair or ActionType.Verify => InstallState
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

    private async Task<List<FileManifest>> VerifyFiles(string installPath, List<FileManifest> fileManifestLists)
    {
        if (!Directory.Exists(CurrentInstall.Location))
        {
            throw new Exception("Invalid installPath provided");
        }
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        // Loop through each file in fileManifest
        var invalidFilesList = new List<FileManifest>();
        await Parallel.ForEachAsync(fileManifestLists, options, async (manifest, token) =>
            {
                // Check if file exists and add to list if it doesn't
                if (!File.Exists(Path.Join(installPath, manifest.Filename)))
                {
                    invalidFilesList.Add(manifest);
                }
                var fileSha1 = Util.CalculateSHA1(Path.Join(installPath, manifest.Filename));
                var expectedHash = BitConverter.ToString(manifest.ShaHash).Replace("-", "").ToLowerInvariant();
                if (fileSha1 != expectedHash)
                {
                    invalidFilesList.Add(manifest);
                    return;
                }
            });
        return invalidFilesList;
    }

    /// <summary>
    /// Retrieves the chunks to download from the file manifest list
    /// </summary>
    /// <param name="manifestData"></param>
    /// <param name="data"></param>
    private void GetChunksToDownload(GetGameManifest manifestData, Manifest data)
    {
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
    }

    public void UpdateInstallProgress(long ioTaskSize)
    {
        lock (_installItemLock)
        {
            CurrentInstall.WrittenSize += ioTaskSize / 1000000.0;
            CurrentInstall.WriteSpeed = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
                ? Math.Round(CurrentInstall.WrittenSize / _installStopWatch.Elapsed.TotalSeconds, 2)
                : 0;
            CurrentInstall.ProgressPercentage = Convert.ToInt32((CurrentInstall.WrittenSize / CurrentInstall.TotalWriteSizeMb) * 100);

            // Limit firing progress update events
            if ((DateTime.Now - _lastUpdateTime).TotalMilliseconds >= _progressUpdateIntervalInMS)
            {
                _lastUpdateTime = DateTime.Now;
                InstallProgressUpdate?.Invoke(CurrentInstall);
            }
        }
    }

    public void TryCheckInstallFinished()
    {
        if (_chunkPartReferences.Count <= 0 && _downloadQueue.IsEmpty && CurrentInstall != null)
        {
            _log.Information("ProcessIoQueue: Both queues are empty. Starting finalize stage");
            _ = UpdateInstalledGameStatus();
        }
    }

    public async Task FailInstall()
    {
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
