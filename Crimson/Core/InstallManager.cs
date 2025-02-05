using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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

    private readonly ILogger _logger;
    private readonly LibraryManager _libraryManager;
    private readonly DownloadManager _downloadManager;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;

    private readonly List<InstallItem> _installQueue = [];
    private readonly List<InstallItem> _installHistory = [];

    private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();
    private ConcurrentDictionary<BigInteger, List<FileManifest>> _chunkToFileManifestsDictionary = new();
    private ConcurrentDictionary<BigInteger, int> _chunkPartReferences = new();
    private readonly HashSet<string> _ioQueueTaskSet = [];
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    private readonly object _installItemLock = new();
    private readonly int _numberOfThreads;
    private const int _progressUpdateIntervalInMS = 1000;

    private BlockingCollection<DownloadTask> _downloadQueue = [];
    private BlockingCollection<IoTask> _ioQueue = [];
    private BlockingCollection<BigInteger> _completedChunks = []; // Chunks that are downloaded and data written to all dependent files
    private List<Task> _downloadTasks;
    private List<Task> _installTasks;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Stopwatch _installStopWatch = new();
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);


    public InstallItem CurrentInstall { get; private set; }

    public InstallManager(ILogger logger, LibraryManager libraryManager, IStoreRepository repository, Storage storage,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _downloadManager = downloadManager;
        CurrentInstall = null;
        _repository = repository;
        _storage = storage;

        _numberOfThreads = 12;
        _jsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new BigIntegerJsonConverter() }
        };
    }

    /// <summary>
    /// Adds game to the install queue.
    /// Starts processing it immediately if no other game is in the queue
    /// </summary>
    /// <param name="item"></param>
    public void AddToQueue(InstallItem item)
    {
        if (item == null)
            return;

        // check if the game is already in the queue
        if (_installQueue.Contains(item, new InstallItemComparer()))
        {
            _logger.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
            return;
        }

        // Check if the game we are trying to install exists in the library
        var gameData = _libraryManager.GetGameInfo(item.AppName);
        if (gameData == null)
        {
            _logger.Warning("AddToQueue: Game {Name} not found in library", item.AppName);
            return;
        }

        if (item.Action != ActionType.Install && gameData.InstallStatus == InstallState.NotInstalled)
        {
            _logger.Warning($"AddToQueue: {item.AppName} is not installed, cannot {item.Action.ToString()}");
            return;
        }

        if (item.Action != ActionType.Repair && item.Action != ActionType.Uninstall && gameData.InstallStatus == InstallState.Broken)
        {
            _logger.Warning($"AddToQueue: {item.AppName} is broken, forcing repair");
            item.Action = ActionType.Repair;
        }

        if (gameData.IsDlc())
        {
            _logger.Warning($"AddToQueue: {item.AppName} is a DLC. DLC Handling is disabled right now");
            return;
        }

        _logger.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        _installQueue.Add(item);
        if (CurrentInstall == null)
            ProcessNext();
    }

    private async void ProcessNext(bool isResuming = false)
    {
        try
        {
            if (isResuming == false && (CurrentInstall != null || _installQueue.Count <= 0)) return;

            if (!isResuming)
            {
                await PrepareTasks();
            }

            CurrentInstall!.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            // Reset cancellation token
            _cancellationTokenSource = new CancellationTokenSource();
            _installStopWatch.Start();
            _pauseEvent.Set();

            _downloadTasks = Enumerable.Range(0, _numberOfThreads)
                .Select(_ => Task.Run(ProcessDownloadQueue, _cancellationTokenSource.Token))
                .ToList();

            _installTasks = Enumerable.Range(0, _numberOfThreads)
                .Select(_ => Task.Run(ProcessIOQueue, _cancellationTokenSource.Token))
                .ToList();

            _downloadQueue.CompleteAdding();

            await Task.WhenAll(_downloadTasks);
            _ioQueue.CompleteAdding();
            await Task.WhenAll(_installTasks);

            await UpdateInstalledGameStatus();

        }
        catch (Exception ex)
        {
            _logger.Error("ProcessNext: {Exception}", ex);
            await HandleInstallationStoppage("An error occurred during installation");
        }
    }

    private async Task PrepareTasks(bool isResuming = false, List<BigInteger> downloadedChunks = null)
    {
        try
        {
            if (!isResuming)
            {
                CurrentInstall = _installQueue[0];
                _installQueue.RemoveAt(0);
            }

            if (CurrentInstall == null) return;
            _logger.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            var manifestData = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
                gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);

            gameData.BaseUrls = manifestData.BaseUrls;
            _storage.SaveMetaData(gameData);

            _logger.Information("ProcessNext: Parsing game manifest");
            var data = Manifest.ReadAll(manifestData.ManifestBytes);

            // TODO Handle stats if game is installed


            if (CurrentInstall.Action == ActionType.Install)
            {
                // create CurrentInstall.folder if it doesn't exist
                if (!Directory.Exists(CurrentInstall.Location))
                {
                    Directory.CreateDirectory(CurrentInstall.Location);
                    _logger.Debug("Folder created at: {location}", CurrentInstall.Location);
                }
            }

            if (!HasFolderWritePermissions(CurrentInstall.Location))
            {
                await HandleInstallationStoppage("No write permissions to install location");
                return;
            }

            ResetQueues();

            if (CurrentInstall.Action == ActionType.Install)
            {
                await _downloadManager.InitializeMirrors(manifestData.BaseUrls);
                GetChunksToDownload(manifestData, data, downloadedChunks);
            }
            else if (CurrentInstall.Action == ActionType.Uninstall)
            {
                foreach (var fileManifest in data.FileManifestList.Elements)
                {
                    CurrentInstall.TotalWriteSizeMb += fileManifest.FileSize / 1024.0 / 1024.0;

                    var task = new IoTask()
                    {
                        DestinationFilePath = Path.Combine(CurrentInstall.Location, fileManifest.Filename),
                        TaskType = IoTaskType.Delete,
                        Size = fileManifest.FileSize,
                    };
                    _logger.Debug("ProcessNext: Adding ioTask: {task}", task);
                    _ioQueue.Add(task);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Exception occured");
        }
    }

    private void ResetQueues()
    {
        _downloadQueue.Dispose();
        _ioQueue.Dispose();
        _downloadQueue = [];
        _ioQueue = [];
        _ioQueueTaskSet.Clear();
    }

    private async Task ProcessDownloadQueue()
    {
        try
        {
            foreach (var downloadTask in _downloadQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                try
                {

                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    _logger.Debug("ProcessDownloadQueue: Downloading chunk with guid{guid} from {url} to {path}", downloadTask.GuidNum, downloadTask.Url, downloadTask.TempPath);
                    await _downloadManager.DownloadFileWithFallback(downloadTask.Url, downloadTask.TempPath);

                    UpdateDownloadProgress(downloadTask.ChunkInfo.FileSize);
                    CreateIoTasksForChunk(downloadTask);
                }
                catch (Exception ex)
                {
                    _logger.Error("ProcessDownloadQueue: Exception: {ex}", ex);
                    await HandleInstallationStoppage("Download task failed");
                }
            }
        }
        // only exception happening here wll be the cancellation token being called
        // just handle it not make application crash
        catch (Exception)
        {
            return;
        }
    }

    private void CreateIoTasksForChunk(DownloadTask downloadTask)
    {
        // get file manifest from dictionary
        var fileManifests = _chunkToFileManifestsDictionary[downloadTask.GuidNum];
        foreach (var fileManifest in fileManifests)
        {
            foreach (var part in fileManifest.ChunkParts)
            {
                if (part.GuidNum != downloadTask.GuidNum) continue;

                // mandatory check to prevent duplicate io tasks
                var ioTaskHashString = $"{fileManifest.Filename}.{part.GuidNum}.{part.FileOffset}";
                if (_ioQueueTaskSet.Contains(ioTaskHashString))
                {
                    continue;
                }
                _ioQueueTaskSet.Add(ioTaskHashString);

                var task = new IoTask()
                {
                    SourceFilePath = downloadTask.TempPath,
                    DestinationFilePath = Path.Combine(CurrentInstall.Location, fileManifest.
                    Filename),
                    TaskType = IoTaskType.Copy,
                    Size = part.Size,
                    Offset = part.Offset,
                    FileOffset = part.FileOffset,
                    GuidNum = part.GuidNum,
                    SourceChunkGuidNum = downloadTask.GuidNum
                };
                _logger.Debug("ProcessDownloadQueue: Adding ioTask {task}", task);
                _ioQueue.Add(task);
            }
        }
    }

    private async Task ProcessIOQueue()
    {
        try
        {
            foreach (var ioTask in _ioQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                try
                {
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    switch (ioTask.TaskType)
                    {
                        case IoTaskType.Copy:
                            await ProcessCopyTask(ioTask);
                            break;
                        case IoTaskType.Delete:
                            File.Delete(ioTask.DestinationFilePath);
                            break;
                    }
                    UpdateInstallWriteProgress(ioTask.Size);
                }
                catch (Exception ex)
                {
                    _logger.Error("ProcessIoQueue: IO task failed with exception {ex}", ex);
                    await HandleInstallationStoppage("Io Task failed");
                }

            }
        }
        // only exception happening here wll be the cancellation token being called
        // just handle it not make application crash
        catch (Exception)
        {
            return;
        }
    }

    private async Task ProcessCopyTask(IoTask ioTask)
    {

        EnsureDirectoryExists(ioTask.DestinationFilePath);

        // Ensure there is a lock object for each destination file
        var fileLock =
            _fileLocksConcurrentDictionary.GetOrAdd(ioTask.DestinationFilePath, new object());

        var compressedChunkData = await File.ReadAllBytesAsync(ioTask.SourceFilePath);
        var chunk = Chunk.ReadBuffer(compressedChunkData);
        _logger.Debug("ProcessIoQueue: Reading chunk buffers from {source} finished", ioTask.SourceFilePath);

        lock (fileLock)
        {
            using var fileStream = new FileStream(ioTask.DestinationFilePath, FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None);

            _logger.Debug("ProcessIoQueue: Seeking {seek}bytes on file {destination}", ioTask.FileOffset, ioTask.DestinationFilePath);
            fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);

            // Since chunk offset is a long we cannot use it directly in File stream write or read
            // Use a memory stream to seek to the chunk offset
            using var memoryStream = new MemoryStream(chunk.Data);
            memoryStream.Seek(ioTask.Offset, SeekOrigin.Begin);

            var remainingBytesToWrite = ioTask.Size;
            // Buffer size is irrelevant as write is continuous
            const int bufferSize = 4096;
            var buffer = new byte[bufferSize];

            _logger.Debug("ProcessIoQueue: Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);

            while (remainingBytesToWrite > 0)
            {
                var bytesToRead = (int)Math.Min(bufferSize, remainingBytesToWrite);
                var bytesRead = memoryStream.Read(buffer, 0, bytesToRead);
                fileStream.Write(buffer, 0, bytesRead);

                remainingBytesToWrite -= bytesRead;
            }

            fileStream.Flush();
            _logger.Debug("ProcessIoQueue: Finished Writing {size}bytes to {file}", ioTask.Size, ioTask.DestinationFilePath);
        }

        // Check for references to the chunk and decrement by one
        int newCount = _chunkPartReferences.AddOrUpdate(
            ioTask.GuidNum,
            (key) => 0, // Not expected to be called as the key should exist
            (key, oldValue) =>
            {
                _logger.Debug("ProcessIoQueue: decrementing reference count of {GuidNum} by 1. Current value:{oldValue}", ioTask.GuidNum, oldValue);
                return oldValue - 1;
            }
        );

        // Check if the updated count is 0 or less
        if (newCount <= 0 && _chunkPartReferences.TryRemove(ioTask.GuidNum, out _))
        {
            _completedChunks.Add(ioTask.SourceChunkGuidNum);
            _logger.Debug("ProcessIoQueue: Deleting chunk file {file}", ioTask.SourceFilePath);
            // Delete the file if successfully removed
            File.Delete(ioTask.SourceFilePath);
        }
    }

    private void EnsureDirectoryExists(string filePath)
    {

        var directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
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
            if (!IsInstallationInProgress())
                return;

            // Intentional delay to wait till all files are written
            await Task.Delay(2000);

            // Stop all queues before doing anything
            await _cancellationTokenSource.CancelAsync();
            _installStopWatch.Reset();

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            if (gameData == null)
            {
                _logger.Error("UpdateInstalledGameStatus: Found no game data for app name: {AppName}",
                    CurrentInstall.AppName);
                throw new Exception("Invalid game data");
            }
            var installedGamesDictionary = _storage.InstalledGamesDictionary;
            installedGamesDictionary ??= [];

            if (installedGamesDictionary.Count > 0 && CurrentInstall.Action == ActionType.Uninstall)
            {
                installedGamesDictionary.Remove(CurrentInstall.AppName);
                _logger.Information("UpdateInstalledGameStatus: Removing entry: {appName} from installed games list", CurrentInstall.AppName);
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
                _logger.Information("UpdateInstalledGameStatus: Verification successful for {appName}", CurrentInstall.AppName);

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

                _logger.Information("UpdateInstalledGameStatus: Adding new entry installed games list {@entry}",
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

            CurrentInstall.Status = ActionStatus.Success;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            CurrentInstall = null;
            ProcessNext();
        }
        catch (Exception ex)
        {
            _logger.Fatal("UpdateInstalledGameStatus: Exception {ex}", ex);

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
    private void GetChunksToDownload(GetGameManifest manifestData, Manifest data, List<BigInteger> chunksToSkip = null)
    {
        var addedChunkGuids = new HashSet<BigInteger>();
        var chunkDownloadList = new List<ChunkInfo>();
        double totalWrittenSize = 0;

        foreach (var fileManifest in data.FileManifestList.Elements)
        {
            foreach (var chunkPart in fileManifest.ChunkParts)
            {
                if (chunksToSkip != null && chunksToSkip.FirstOrDefault(chunk => chunk == chunkPart.GuidNum) != 0)
                {
                    // Add up file sizes of all chunks written to subtract from total
                    totalWrittenSize += chunkPart.Size;
                    continue;
                }

                if (_chunkToFileManifestsDictionary.TryGetValue(chunkPart.GuidNum, out var fileManifests))
                {
                    fileManifests.Add(fileManifest);
                    _chunkToFileManifestsDictionary[chunkPart.GuidNum] = fileManifests;
                }
                else
                {
                    _ = _chunkToFileManifestsDictionary.TryAdd(chunkPart.GuidNum,
                        new List<FileManifest>() { fileManifest });
                }

                _logger.Debug("ProcessDownloadQueue: New file reference for chunk {GuidNum} filename:{filename}", chunkPart.GuidNum, fileManifest.Filename);
                // keep track of files count to which the parts of chunk must be copied to
                _chunkPartReferences.AddOrUpdate(
                    chunkPart.GuidNum,
                    1, // Add with a count of 1 if not present
                    (key, oldValue) => oldValue + 1 // Update: increment the count
                );

                if (addedChunkGuids.Contains(chunkPart.GuidNum))
                {
                    continue;
                }

                addedChunkGuids.Add(chunkPart.GuidNum);
                var chunkInfo = data.CDL.GetChunkByGuidNum(chunkPart.GuidNum);
                var newTask = new DownloadTask()
                {
                    Url = chunkInfo.Path,
                    TempPath = Path.Combine(CurrentInstall.Location, ".Crimson", (chunkInfo.GuidNum + ".chunk")),
                    GuidNum = chunkInfo.GuidNum,
                    ChunkInfo = chunkInfo
                };
                _logger.Debug("ProcessNext: Adding new download task {@task}", newTask);
                chunkDownloadList.Add(chunkInfo);
                _downloadQueue.Add(newTask);

                CurrentInstall.TotalDownloadSizeBytes += chunkInfo.FileSize;
            }
            CurrentInstall.TotalWriteSizeBytes += fileManifest.FileSize;
        }
        CurrentInstall.TotalWriteSizeBytes -= totalWrittenSize;
        CurrentInstall.TotalDownloadSizeMiB = CurrentInstall.TotalDownloadSizeBytes / 1024.0 / 1024.0;
        CurrentInstall.TotalWriteSizeMb = CurrentInstall.TotalWriteSizeBytes / 1024.0 / 1024.0;
    }

    private void UpdateDownloadProgress(long downloadedSize)
    {
        if (!IsInstallationInProgress())
            return;
        lock (_installItemLock)
        {
            CurrentInstall.DownloadedSizeMiB += downloadedSize / 1024.0 / 1024.0;
            CurrentInstall.DownloadSpeedRawMiB = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
                ? Math.Round(CurrentInstall.DownloadedSizeMiB / _installStopWatch.Elapsed.TotalSeconds, 2)
                : 0;

            UpdateProgressIfNeeded();
        }
    }

    private void UpdateInstallWriteProgress(long ioTaskSize)
    {
        if (!IsInstallationInProgress())
            return;
        lock (_installItemLock)
        {
            CurrentInstall.WrittenSizeMiB += ioTaskSize / 1024.0 / 1024.0;

            // bad very bad, should not happen
            if (CurrentInstall.TotalWriteSizeMb < CurrentInstall.WrittenSizeMiB)
            {
                return;
            }

            CurrentInstall.WriteSpeedMiB = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
                ? Math.Round(CurrentInstall.WrittenSizeMiB / _installStopWatch.Elapsed.TotalSeconds, 2)
                : 0;
            CurrentInstall.ProgressPercentage = Convert.ToInt32((CurrentInstall.WrittenSizeMiB / CurrentInstall.TotalWriteSizeMb) * 100);
            UpdateProgressIfNeeded();
        }
    }
    private void UpdateProgressIfNeeded()
    {
        if (!IsInstallationInProgress())
            return;
        // Limit firing progress update events
        if ((DateTime.Now - _lastUpdateTime).TotalMilliseconds >= _progressUpdateIntervalInMS)
        {
            _lastUpdateTime = DateTime.Now;
            InstallProgressUpdate?.Invoke(CurrentInstall);
        }
    }

    private async Task HandleInstallationStoppage(string errorMessage)
    {
        _ioQueue = new();
        _downloadQueue = new();

        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            // propage cancelling status if not done already
            await _cancellationTokenSource.CancelAsync();
            CurrentInstall.Status = ActionStatus.Cancelling;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            await Task.WhenAll(_downloadTasks);
            await Task.WhenAll(_installTasks);

            CurrentInstall.Status = ActionStatus.Failed;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            _logger.Error("Installation failed: {ErrorMessage}", errorMessage);
        }
        else
        {
            CurrentInstall.Status = ActionStatus.Cancelling;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            await Task.WhenAll(_downloadTasks);
            await Task.WhenAll(_installTasks);

            // clear downloaded files
            Directory.Delete(CurrentInstall.Location, true);

            CurrentInstall.Status = ActionStatus.Cancelled;
            InstallationStatusChanged?.Invoke(CurrentInstall);
            _logger.Error("Installation cancelled");
        }
        CurrentInstall = null;

        var state = new InstallManagerState
        {
            CurrentInstall = null,
            IoQueue = null,
            CompletedChunks = null
        };

        var json = JsonSerializer.Serialize(state, _jsonSerializerOptions);
        _storage.SaveInstallState(json);

        ProcessNext();
    }

    /// <summary>
    ///  Retrieve the Total Size to download and as well as space for install
    /// </summary>
    /// <param name="appName"></param>
    /// <returns></returns>
    public async Task<(double totalDownloadSizeMb, double totalWriteSizeMb)> GetGameDownloadInstallSizes(string appName)
    {
        _logger.Information($"GetGameDownloadInstallSizes: Getting game manifest of {appName}");
        var gameData = _libraryManager.GetGameInfo(appName);
        var manifestData = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
            gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);

        gameData.BaseUrls = manifestData.BaseUrls;

        _logger.Information($"GetGameDownloadInstallSizes: parsing game manifest of {appName}");
        var manifest = Manifest.ReadAll(manifestData.ManifestBytes);
        var chunkDownloadList = new List<ChunkInfo>();
        var addedChunkGuids = new HashSet<BigInteger>();

        double totalDownloadSizeBytes = 0;
        double totalWriteSizeBytes = 0;

        foreach (var fileManifest in manifest.FileManifestList.Elements)
        {
            foreach (var chunkPart in fileManifest.ChunkParts)
            {
                if (_chunkToFileManifestsDictionary.TryGetValue(chunkPart.GuidNum, out var fileManifests))
                {
                    fileManifests.Add(fileManifest);
                    _chunkToFileManifestsDictionary[chunkPart.GuidNum] = fileManifests;
                }
                else
                {
                    _ = _chunkToFileManifestsDictionary.TryAdd(chunkPart.GuidNum,
                        new List<FileManifest>() { fileManifest });
                }

                if (!addedChunkGuids.Contains(chunkPart.GuidNum))
                {
                    var chunkInfo = manifest.CDL.GetChunkByGuidNum(chunkPart.GuidNum);
                    chunkDownloadList.Add(chunkInfo);
                    addedChunkGuids.Add(chunkPart.GuidNum);

                    totalDownloadSizeBytes += chunkInfo.FileSize;
                }
            }
            totalWriteSizeBytes += fileManifest.FileSize;
        }
        _logger.Information($"GetGameDownloadInstallSizes: parsing total download size as {totalDownloadSizeBytes} Bytes and write size as {totalWriteSizeBytes} Bytes");
        return (totalDownloadSizeBytes, totalWriteSizeBytes);
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

    public void CancelInstall(string appName)
    {
        if (string.IsNullOrEmpty(appName))
        {
            _logger.Warning("RemoveFromQueue: Invalid app name provided");
        }

        if (CurrentInstall?.AppName == appName)
        {
            Task.Run(() => StopProcessing());
        }

        var removedItem = _installQueue.RemoveAll(item => item.AppName == appName);
        if (removedItem > 0)
        {
            _logger.Information("RemoveFromQueue: Removed {AppName} from the install queue", appName);
        }
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
            _logger.Error($"An error occurred: {ex.Message}");
            return false;
        }
    }

    public async Task StopProcessing()
    {
        CurrentInstall.Status = ActionStatus.Cancelling;
        InstallationStatusChanged?.Invoke(CurrentInstall);

        await _cancellationTokenSource.CancelAsync();
        await HandleInstallationStoppage("Cancel install");
    }

    private bool IsInstallationInProgress()
    {
        return CurrentInstall != null &&
            CurrentInstall.Status != ActionStatus.Cancelling &&
            CurrentInstall.Status != ActionStatus.Failed &&
            CurrentInstall.Status != ActionStatus.Cancelled &&
            CurrentInstall.Status != ActionStatus.Paused;
    }

    public void PauseInstall()
    {

        if (IsInstallationInProgress())
        {
            _logger.Debug("Pausing installation of {game}", CurrentInstall.AppName);
            _pauseEvent.Reset();

            Thread.Sleep(2000);

            _installStopWatch.Stop();
            CurrentInstall.Status = ActionStatus.Paused;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            var state = new InstallManagerState
            {
                CurrentInstall = CurrentInstall,
                IoQueue = [.. _ioQueue],
                CompletedChunks = [.. _completedChunks]
            };

            var json = JsonSerializer.Serialize(state, _jsonSerializerOptions);
            _storage.SaveInstallState(json);
            _logger.Information("Saved installation state");
            _logger.Debug("Successfully paused installation of {game}", CurrentInstall.AppName);
        }
        else
            _logger.Warning("Installation of {appName} is not in progress {state}", CurrentInstall.AppName, CurrentInstall.Status);
    }

    public void ResumeInstall()
    {

        if (CurrentInstall.Status == ActionStatus.Paused)
        {
            ProcessNext(true);
            Thread.Sleep(2000);
        }
        else
            _logger.Warning("Installation of {appName} is not paused {state}", CurrentInstall.AppName, CurrentInstall.Status);
    }

    public async Task LoadPendingInstalls()
    {
        string jsonData;
        try
        {
            jsonData = _storage.GetInstallState();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(jsonData))
            return;

        var state = JsonSerializer.Deserialize<InstallManagerState>(jsonData, _jsonSerializerOptions);
        if (state == null) return;

        if (state.CurrentInstall == null) return;

        CurrentInstall = new InstallItem(state.CurrentInstall.AppName, state.CurrentInstall.Action, state.CurrentInstall.Location);

        state.IoQueue.ForEach(task => _ioQueue.Add(task));
        state.CompletedChunks.ForEach(chunk => _completedChunks.Add(chunk));

        await PrepareTasks(true, state.CompletedChunks);

        CurrentInstall.Status = ActionStatus.Paused;
        InstallationStatusChanged?.Invoke(CurrentInstall);

        _pauseEvent.Set();
        //ProcessNext(true);
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

    public BigInteger GuidNum { get; set; }

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
    public BigInteger GuidNum { get; set; }
    public BigInteger SourceChunkGuidNum { get; set; }
}

public enum IoTaskType
{
    Copy,
    Create,
    Delete,
    Read
}

public class InstallManagerState
{
    public InstallItem CurrentInstall { get; set; }
    public List<IoTask> IoQueue { get; set; }
    public List<BigInteger> CompletedChunks { get; set; }
}
