using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Infrastructure;
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
    private readonly IInstallFileSystemProbe _fileSystemProbe;

    private readonly List<InstallItem> _installQueue = [];
    private readonly List<InstallItem> _installHistory = [];

    private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();
    private ConcurrentDictionary<BigInteger, List<FileManifest>> _chunkToFileManifestsDictionary = new();
    private ConcurrentDictionary<BigInteger, int> _chunkPartReferences = new();
    private readonly ConcurrentDictionary<string, byte> _ioQueueTaskSet = new();
    private readonly List<string> _uninstallManifestPaths = [];
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private static readonly JsonStateSchema<UpdateTransactionState> UpdateTransactionSchema =
        new("update-transaction", 1, 16L * 1024 * 1024);

    private readonly object _installItemLock = new();
    private readonly object _operationLifecycleLock = new();
    private readonly int _numberOfThreads;
    private const int _progressUpdateIntervalInMS = 1000;
    private List<FileManifest> _importVerificationResult;

    private BlockingCollection<DownloadTask> _downloadQueue = [];
    private BlockingCollection<IoTask> _ioQueue = [];
    private BlockingCollection<BigInteger> _completedChunks = []; // Chunks that are downloaded and data written to all dependent files
    private List<Task> _downloadTasks;
    private List<Task> _installTasks;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Stopwatch _installStopWatch = new();
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
    private volatile bool _userCancellationRequested;
    private bool _acceptCancellation;
    private TaskCompletionSource _operationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UpdateTransactionState? _updateTransaction;

    internal Action<string>? UpdatePublicationFaultInjector { get; set; }
    internal Action<UpdateTransactionState>? UpdateJournalTransitionFaultInjector { get; set; }

    public InstallItem? CurrentInstall { get; private set; }

    public InstallManager(
        ILogger logger,
        LibraryManager libraryManager,
        IStoreRepository repository,
        Storage storage,
        DownloadManager downloadManager,
        IInstallFileSystemProbe fileSystemProbe)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _downloadManager = downloadManager;
        CurrentInstall = null;
        _repository = repository;
        _storage = storage;
        _fileSystemProbe = fileSystemProbe;

        _numberOfThreads = 12;
        _jsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new BigIntegerJsonConverter() }
        };

        foreach (var installation in _storage.LocalAppStateDictionary.Values)
        {
            if (!string.IsNullOrWhiteSpace(installation.InstallPath))
                RecoverPendingUpdate(installation.InstallPath);
        }
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

        if (item.Action != ActionType.Install && item.Action != ActionType.Import &&
            (gameData.LocalAppState == null || gameData.LocalAppState?.InstallStatus == InstallState.NotInstalled))
        {
            _logger.Warning($"AddToQueue: {item.AppName} is not installed, cannot {item.Action.ToString()}");
            return;
        }

        if (item.Action != ActionType.Repair && item.Action != ActionType.Uninstall && gameData.LocalAppState?.InstallStatus == InstallState.Broken)
        {
            _logger.Warning($"AddToQueue: {item.AppName} is broken, forcing repair");
            item.Action = ActionType.Repair;
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
                lock (_operationLifecycleLock)
                {
                    _userCancellationRequested = false;
                    _installStopWatch.Reset();
                    _acceptCancellation = true;
                    _operationCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _downloadTasks = null;
                    _installTasks = null;
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = new CancellationTokenSource();
                }
                await PrepareTasks();
            }

            // PrepareTasks may call HandleInstallationStoppage (e.g. import folder empty),
            // which sets CurrentInstall = null. Bail out if that happened.
            if (CurrentInstall == null) return;

            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            CurrentInstall.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            // Import and Move don't need download/IO workers
            if (CurrentInstall.Action == ActionType.Import || CurrentInstall.Action == ActionType.Move)
            {
                await UpdateInstalledGameStatus();
                return;
            }

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

            BeginFinalization();
            if (CurrentInstall.Action == ActionType.Update && _updateTransaction != null)
                await CommitPreparedUpdate();
            await UpdateInstalledGameStatus();

        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            await HandleInstallationStoppage(
                _userCancellationRequested ? "Installation cancelled" : "Installation worker cancelled",
                _userCancellationRequested);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ProcessNext: Installation failed");
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

            var manifestData = await GetManifestDataWithCaching(
                CurrentInstall.AppName,
                _cancellationTokenSource.Token);
            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);

            _logger.Information("ProcessNext: Parsing game manifest");
            var data = Manifest.ReadAll(manifestData);

            // TODO Handle stats if game is installed


            if (CurrentInstall.Action == ActionType.Install)
            {
                var installRoot = ManifestPath.RevalidateUnderRoot(
                    CurrentInstall.Location,
                    CurrentInstall.Location);
                if (!Directory.Exists(installRoot))
                {
                    Directory.CreateDirectory(installRoot);
                    _ = ManifestPath.RevalidateUnderRoot(installRoot, installRoot);
                    _logger.Debug("Folder created at: {location}", installRoot);
                }
            }

            var writeProbe = _fileSystemProbe.Probe(CurrentInstall.Location);
            if (!writeProbe.Success)
            {
                _logger.Warning(
                    "Install filesystem probe failed for {AppName} with {ErrorType}",
                    CurrentInstall.AppName,
                    writeProbe.ErrorType);
                if (writeProbe.CleanupFailures is { Count: > 0 } cleanupFailures)
                {
                    _logger.Error(
                        "Install filesystem probe left {Count} cleanup artifacts for {AppName}: {ErrorTypes}",
                        cleanupFailures.Count,
                        CurrentInstall.AppName,
                        string.Join(",", cleanupFailures.Select(failure => failure.ErrorType)));
                }
                await HandleInstallationStoppage("Install location does not support required write operations");
                return;
            }

            if (writeProbe.Location != InstallFileSystemLocation.Local)
            {
                _logger.Warning(
                    "Install filesystem for {AppName} is {Location}; only local filesystems are supported",
                    CurrentInstall.AppName,
                    writeProbe.Location);
                await HandleInstallationStoppage("Install location is not on a supported local filesystem");
                return;
            }

            ResetQueues();

            if (CurrentInstall.Action == ActionType.Install)
            {
                await _downloadManager.InitializeMirrors(
                    gameData.BaseUrls,
                    _cancellationTokenSource.Token);
                GetChunksToDownload(data, downloadedChunks);
            }
            else if (CurrentInstall.Action == ActionType.Update)
            {
                await PrepareUpdateTasks(gameData, data);
            }
            else if (CurrentInstall.Action == ActionType.Repair)
            {
                await PrepareRepairTasks(gameData, data);
            }
            else if (CurrentInstall.Action == ActionType.Uninstall)
            {
                foreach (var fileManifest in data.FileManifestList.Elements)
                {
                    _uninstallManifestPaths.Add(fileManifest.Path.Value);
                    CurrentInstall.TotalWriteSizeMb += fileManifest.FileSize / 1024.0 / 1024.0;

                    var task = new IoTask()
                    {
                        DestinationFilePath = ManifestPath.ResolveUnderRoot(CurrentInstall.Location, fileManifest.Path),
                        TaskType = IoTaskType.Delete,
                        Size = fileManifest.FileSize,
                    };
                    _logger.Debug("ProcessNext: Adding ioTask: {task}", task);
                    _ioQueue.Add(task);
                }
            }
            else if (CurrentInstall.Action == ActionType.Import)
            {
                if (!Directory.Exists(CurrentInstall.Location))
                {
                    await HandleInstallationStoppage("Import folder does not exist");
                    return;
                }

                // Import only checks file existence, not SHA1 hashes.
                // Hash verification would fail if the installed version differs from latest.
                // Users can run Verify/Repair separately after import if needed.
                var missingFiles = new List<FileManifest>();
                foreach (var fileManifest in data.FileManifestList.Elements)
                {
                    var filePath = ManifestPath.ResolveExistingImportFile(
                        CurrentInstall.Location,
                        fileManifest.Path);
                    if (filePath is null)
                        missingFiles.Add(fileManifest);
                }

                _importVerificationResult = missingFiles;

                if (missingFiles.Count == 0)
                {
                    _logger.Information("Import: All {Total} files found for {AppName}",
                        data.FileManifestList.Elements.Count, CurrentInstall.AppName);
                }
                else
                {
                    // Always salvage what we can — import as Broken so user can Repair
                    _logger.Warning("Import: {Missing}/{Total} files missing for {AppName}. Will import as Broken.",
                        missingFiles.Count, data.FileManifestList.Elements.Count, CurrentInstall.AppName);
                }
            }
            else if (CurrentInstall.Action == ActionType.Move)
            {
                var destinationParent = Path.GetDirectoryName(
                    Path.GetFullPath(CurrentInstall.MoveLocation));
                if (string.IsNullOrWhiteSpace(destinationParent))
                {
                    await HandleInstallationStoppage("Move destination has no parent directory");
                    return;
                }
                var destinationProbe = _fileSystemProbe.Probe(destinationParent);
                if (!destinationProbe.Success ||
                    destinationProbe.Location != InstallFileSystemLocation.Local ||
                    string.IsNullOrWhiteSpace(writeProbe.VolumeIdentity) ||
                    !string.Equals(
                        writeProbe.VolumeIdentity,
                        destinationProbe.VolumeIdentity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInstallationStoppage("Cross-volume moves are not supported");
                    return;
                }

                if (Directory.Exists(CurrentInstall.MoveLocation))
                {
                    await HandleInstallationStoppage("Destination directory already exists");
                    return;
                }

                var sourcePath = ManifestPath.RevalidateUnderRoot(
                    CurrentInstall.Location,
                    CurrentInstall.Location);
                var destinationPath = ManifestPath.RevalidateUnderRoot(
                    CurrentInstall.MoveLocation,
                    CurrentInstall.MoveLocation);
                _logger.Information("Move: Moving {AppName} from {Src} to {Dest}",
                    CurrentInstall.AppName, sourcePath, destinationPath);
                BeginFinalization();
                Directory.Move(sourcePath, destinationPath);
                _logger.Information("Move: Successfully moved {AppName}", CurrentInstall.AppName);
            }

            if (CurrentInstall.Action is ActionType.Install or ActionType.Update or ActionType.Repair &&
                (writeProbe.AvailableBytes is not long availableBytes ||
                 CurrentInstall.TotalWriteSizeBytes > availableBytes))
            {
                await HandleInstallationStoppage("Install location does not have enough available space");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PrepareTasks: Exception occured while preparing tasks");
            throw;
        }
    }

    private void ResetQueues()
    {
        _downloadQueue.Dispose();
        _ioQueue.Dispose();
        _downloadQueue = [];
        _ioQueue = [];
        _ioQueueTaskSet.Clear();
        _updateTransaction = null;
        _uninstallManifestPaths.Clear();
        _chunkToFileManifestsDictionary = new();
        _chunkPartReferences = new();
        _completedChunks.Dispose();
        _completedChunks = [];
        _fileLocksConcurrentDictionary.Clear();
    }

    private async Task ProcessDownloadQueue()
    {
        try
        {
            foreach (var downloadTask in _downloadQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                _pauseEvent.Wait(_cancellationTokenSource.Token);

                _logger.Debug("ProcessDownloadQueue: Downloading chunk with guid{guid} from {url} to {path}",
                    downloadTask.GuidNum, downloadTask.Url, downloadTask.TempPath);
                var success = await _downloadManager.DownloadFileWithFallback(
                    downloadTask.Url,
                    downloadTask.TempPath,
                    cancellationToken: _cancellationTokenSource.Token,
                    expectedSize: downloadTask.ChunkInfo.FileSize);
                if (!success)
                    throw new IOException($"Failed to download chunk {downloadTask.GuidNum} from all mirrors");

                var downloadedChunkBytes = await File.ReadAllBytesAsync(
                    downloadTask.TempPath,
                    _cancellationTokenSource.Token);
                var downloadedChunk = Chunk.ReadBuffer(downloadedChunkBytes);
                downloadedChunk.ValidateAgainst(downloadTask.ChunkInfo);

                UpdateDownloadProgress(downloadTask.ChunkInfo.FileSize);
                CreateIoTasksForChunk(downloadTask);
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ProcessDownloadQueue: Download worker failed");
            _cancellationTokenSource.Cancel();
            throw;
        }
    }

    private void CreateIoTasksForChunk(DownloadTask downloadTask)
    {
        // get file manifest from dictionary
        var writeRoot = _updateTransaction?.StagingRoot ?? CurrentInstall.Location;
        var fileManifests = _chunkToFileManifestsDictionary[downloadTask.GuidNum];
        foreach (var fileManifest in fileManifests)
        {
            foreach (var part in fileManifest.ChunkParts)
            {
                if (part.GuidNum != downloadTask.GuidNum) continue;

                // mandatory check to prevent duplicate io tasks
                var ioTaskHashString = $"{fileManifest.Path}.{part.GuidNum}.{part.FileOffset}";
                if (!_ioQueueTaskSet.TryAdd(ioTaskHashString, 0))
                    continue;

                var task = new IoTask()
                {
                    SourceFilePath = downloadTask.TempPath,
                    DestinationFilePath = ManifestPath.ResolveUnderRoot(writeRoot, fileManifest.Path),
                    TaskType = IoTaskType.Copy,
                    Size = part.Size,
                    DestinationFileSize = fileManifest.FileSize,
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
                _pauseEvent.Wait(_cancellationTokenSource.Token);

                switch (ioTask.TaskType)
                {
                    case IoTaskType.Copy:
                        await ProcessCopyTask(ioTask);
                        break;
                    case IoTaskType.Delete:
                        var deletePath = ManifestPath.RevalidateUnderRoot(
                            CurrentInstall.Location,
                            ioTask.DestinationFilePath);
                        File.Delete(deletePath);
                        break;
                }
                UpdateInstallWriteProgress(ioTask.Size);
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ProcessIoQueue: IO worker failed");
            _cancellationTokenSource.Cancel();
            throw;
        }
    }

    private async Task ProcessCopyTask(IoTask ioTask)
    {
        var writeRoot = _updateTransaction?.StagingRoot ?? CurrentInstall.Location;
        var destinationPath = ManifestPath.RevalidateUnderRoot(
            writeRoot,
            ioTask.DestinationFilePath);
        EnsureDirectoryExists(destinationPath);
        destinationPath = ManifestPath.RevalidateUnderRoot(writeRoot, destinationPath);

        // Ensure there is a lock object for each destination file
        var fileLock = _fileLocksConcurrentDictionary.GetOrAdd(destinationPath, new object());

        var compressedChunkData = await File.ReadAllBytesAsync(
            ioTask.SourceFilePath, _cancellationTokenSource.Token);
        var chunk = Chunk.ReadBuffer(compressedChunkData);
        _logger.Debug("ProcessIoQueue: Reading chunk buffers from {source} finished", ioTask.SourceFilePath);

        if (ioTask.Offset < 0 || ioTask.Size < 0 || ioTask.FileOffset < 0 || ioTask.DestinationFileSize < 0 ||
            ioTask.Offset > chunk.Data.LongLength || ioTask.Size > chunk.Data.LongLength - ioTask.Offset ||
            ioTask.FileOffset > ioTask.DestinationFileSize ||
            ioTask.Size > ioTask.DestinationFileSize - ioTask.FileOffset)
        {
            throw new InvalidDataException($"Invalid chunk range for {destinationPath}");
        }

        lock (fileLock)
        {
            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            using var fileStream = new FileStream(destinationPath, FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None);
            fileStream.SetLength(ioTask.DestinationFileSize);

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
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                memoryStream.ReadExactly(buffer, 0, bytesToRead);
                fileStream.Write(buffer, 0, bytesToRead);

                remainingBytesToWrite -= bytesToRead;
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
            var chunkPath = ManifestPath.RevalidateUnderRoot(
                CurrentInstall.Location,
                ioTask.SourceFilePath);
            File.Delete(chunkPath);
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
        if (CurrentInstall == null)
        {
            _logger.Fatal("UpdateInstalledGameStatus: Current Install is null. Shits bad");
            return;
        }

        try
        {
            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            if (!IsInstallationInProgress())
                return;

            // Only delay for actions that used download/IO workers
            if (CurrentInstall.Action != ActionType.Import && CurrentInstall.Action != ActionType.Move)
                await Task.Delay(2000, _cancellationTokenSource.Token);
            _installStopWatch.Reset();

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            if (gameData == null)
            {
                _logger.Error("UpdateInstalledGameStatus: Found no game data for app name: {AppName}",
                    CurrentInstall.AppName);
                throw new Exception("Invalid game data");
            }

            // For Import, create LocalAppState if it doesn't exist yet
            if (!_storage.LocalAppStateDictionary.TryGetValue(CurrentInstall.AppName, out var localAppState))
            {
                if (CurrentInstall.Action == ActionType.Import)
                {
                    localAppState = new LocalAppState { AppName = CurrentInstall.AppName };
                }
                else
                {
                    _logger.Fatal("UpdateInstalledGameStatus: Found no installed game data for app name: {AppName}",
                        CurrentInstall.AppName);
                    throw new Exception("Invalid installed game data");
                }
            }

            switch (CurrentInstall.Action)
            {
                case ActionType.Uninstall:
                {
                    // No verification needed — files are already deleted
                    BeginFinalization();
                    InstallDirectoryCleanup.RemoveEmptyOwnedDirectories(
                        CurrentInstall.Location,
                        _uninstallManifestPaths);
                    localAppState.InstallStatus = InstallState.NotInstalled;
                    localAppState.InstallPath = null;
                    localAppState.Version = null;
                    localAppState.Executable = null;
                    gameData.LocalAppState = localAppState;
                    _storage.AddToLocalAppState(gameData.AppName, localAppState);
                    _libraryManager.UpdateGameInfo(gameData);
                    _logger.Information("UpdateInstalledGameStatus: Uninstall complete for {AppName}", CurrentInstall.AppName);
                    break;
                }

                case ActionType.Move:
                {
                    // No verification needed — just update the install path
                    BeginFinalization();
                    localAppState.InstallPath = CurrentInstall.MoveLocation;
                    gameData.LocalAppState = localAppState;
                    _storage.AddToLocalAppState(gameData.AppName, localAppState);
                    _libraryManager.UpdateGameInfo(gameData);
                    _logger.Information("UpdateInstalledGameStatus: Move complete for {AppName}", CurrentInstall.AppName);
                    break;
                }

                case ActionType.Import:
                {
                    // Files were verified in PrepareTasks — use stored result
                    var manifestBytes = await GetManifestDataWithCaching(
                        CurrentInstall.AppName,
                        _cancellationTokenSource.Token);
                    var manifestData = Manifest.ReadAll(manifestBytes);

                    var canRunOffLine = gameData.Metadata?.CustomAttributes?.CanRunOffline?.Value == "true";
                    var requireOwnerShipToken = gameData.Metadata?.CustomAttributes?.OwnershipToken?.Value == "true";

                    BeginFinalization();
                    localAppState.InstallStatus = (_importVerificationResult != null && _importVerificationResult.Count > 0)
                        ? InstallState.Broken
                        : InstallState.Installed;
                    localAppState.BaseUrls = gameData.BaseUrls;
                    localAppState.CanRunOffline = canRunOffLine;
                    localAppState.Executable = manifestData.ManifestMeta.LaunchExe.Value;
                    localAppState.InstallPath = CurrentInstall.Location;
                    localAppState.LaunchParameters = manifestData.ManifestMeta.LaunchCommand;
                    localAppState.RequiresOt = requireOwnerShipToken;
                    localAppState.Version = manifestData.ManifestMeta.BuildVersion;
                    localAppState.Title = gameData.AppTitle;

                    if (manifestData.ManifestMeta.UninstallActionPath is { } uninstallPath)
                    {
                        localAppState.Uninstaller = new Dictionary<string, string>
                        {
                            { uninstallPath.Value, manifestData.ManifestMeta.UninstallActionArgs }
                        };
                    }

                    gameData.LocalAppState = localAppState;
                    _storage.AddToLocalAppState(gameData.AppName, localAppState);
                    _libraryManager.UpdateGameInfo(gameData);

                    var totalFiles = manifestData.FileManifestList.Elements.Count;
                    var missingCount = _importVerificationResult?.Count ?? 0;
                    CurrentInstall.StatusMessage = $"Verified {totalFiles} files: {totalFiles - missingCount} found, {missingCount} missing";

                    _importVerificationResult = null;
                    _logger.Information("UpdateInstalledGameStatus: Import complete for {AppName}, status: {Status}",
                        CurrentInstall.AppName, localAppState.InstallStatus);
                    break;
                }

                default: // Install, Update, Repair
                {
                    var manifestBytes = await GetManifestDataWithCaching(
                        CurrentInstall.AppName,
                        _cancellationTokenSource.Token);
                    var manifestData = Manifest.ReadAll(manifestBytes);

                    // Verify all the files
                    var invalidFilesList = await VerifyFiles(
                        CurrentInstall.Location,
                        manifestData.FileManifestList.Elements,
                        _cancellationTokenSource.Token);

                    if (invalidFilesList.Count > 0 && CurrentInstall.Action == ActionType.Update)
                        throw new InvalidDataException("Published update failed whole-installation verification.");
                    BeginFinalization();
                    localAppState = BuildInstalledLocalState(
                        gameData,
                        manifestData,
                        localAppState,
                        CurrentInstall.Location);
                    if (invalidFilesList.Count > 0)
                    {
                        _logger.Warning("UpdateInstalledGameStatus: {Count} files failed verification for {AppName}. Marking as Broken.",
                            invalidFilesList.Count, CurrentInstall.AppName);
                        localAppState.InstallStatus = InstallState.Broken;
                    }
                    else
                    {
                        _logger.Information("UpdateInstalledGameStatus: Verification successful for {appName}", CurrentInstall.AppName);
                        localAppState.InstallStatus = InstallState.Installed;
                    }

                    gameData.LocalAppState = localAppState;
                    _storage.AddToLocalAppState(gameData.AppName, localAppState);
                    _libraryManager.UpdateGameInfo(gameData);
                    break;
                }
            }

            if (CurrentInstall.Action == ActionType.Update && _updateTransaction != null)
            {
                MarkUpdateMetadataCommitted();
                CompletePreparedUpdate();
            }

            CurrentInstall.Status = ActionStatus.Success;
            _installHistory.Add(CurrentInstall);
            InstallationStatusChanged?.Invoke(CurrentInstall);
            CompleteCurrentOperation();
            ProcessNext();
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Fatal("UpdateInstalledGameStatus: Exception {ex}", ex);

            if (_updateTransaction != null)
            {
                try
                {
                    RollbackPreparedUpdate();
                }
                catch (Exception rollbackException)
                {
                    _logger.Fatal(rollbackException, "Update rollback failed; startup recovery is required");
                }
            }

            if (CurrentInstall != null)
            {
                CurrentInstall.Status = ActionStatus.Failed;
                _installHistory.Add(CurrentInstall);
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }
            CompleteCurrentOperation();
            ProcessNext();
        }
    }

    private async Task<List<FileManifest>> VerifyFiles(
        string installPath,
        List<FileManifest> fileManifestLists,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(installPath))
            throw new DirectoryNotFoundException($"Verification root does not exist: {installPath}");
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        // Loop through each file in fileManifest
        var invalidFilesBag = new ConcurrentBag<FileManifest>();
        await Parallel.ForEachAsync(fileManifestLists, options, async (manifest, token) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var filePath = ManifestPath.ResolveUnderRoot(installPath, manifest.Path);

                    // Check if file exists and add to list if it doesn't
                    if (!File.Exists(filePath))
                    {
                        _logger.Warning("VerifyFiles: MISSING {Filename}", manifest.Path);
                        invalidFilesBag.Add(manifest);
                        return;
                    }

                    var fileSha1 = Util.CalculateSHA1(filePath);
                    token.ThrowIfCancellationRequested();
                    var expectedHash = BitConverter.ToString(manifest.ShaHash).Replace("-", "").ToLowerInvariant();
                    if (fileSha1 != expectedHash)
                    {
                        var fileInfo = new FileInfo(filePath);
                        _logger.Warning("VerifyFiles: HASH MISMATCH {Filename} (size={Size}, expected={Expected}, actual={Actual})",
                            manifest.Path, fileInfo.Length, expectedHash, fileSha1);
                        invalidFilesBag.Add(manifest);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "VerifyFiles: Error verifying file {Filename}", manifest.Path);
                    invalidFilesBag.Add(manifest);
                }
            });
        return invalidFilesBag.ToList();
    }

    /// <summary>
    /// Retrieves the chunks to download from the file manifest list
    /// </summary>
    /// <param name="manifestData"></param>
    /// <param name="data"></param>
    private void GetChunksToDownload(Manifest data, List<BigInteger> chunksToSkip = null)
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

                _logger.Debug("ProcessDownloadQueue: New file reference for chunk {GuidNum} filename:{filename}", chunkPart.GuidNum, fileManifest.Path);
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

        // Create empty files (manifest entries with 0 chunks, e.g. DO_NOT_DELETE.txt)
        foreach (var fileManifest in data.FileManifestList.Elements)
        {
            if (fileManifest.ChunkParts.Count == 0)
            {
                var filePath = ManifestPath.ResolveUnderRoot(CurrentInstall.Location, fileManifest.Path);
                EnsureDirectoryExists(filePath);
                filePath = ManifestPath.RevalidateUnderRoot(CurrentInstall.Location, filePath);
                File.Create(filePath).Dispose();
                _logger.Debug("GetChunksToDownload: Created empty file {Path}", filePath);
            }
        }
    }

    /// <summary>
    /// Prepare update tasks by comparing old and new manifests.
    /// Downloads only chunks for changed/added files and deletes removed files.
    /// Falls back to full reinstall if old manifest is unavailable.
    /// </summary>
    private async Task PrepareUpdateTasks(Game gameData, Manifest newManifest)
    {
        // Try to load the old manifest for the currently installed version
        var localAppState = _storage.LocalAppStateDictionary
            .FirstOrDefault(g => g.Key == CurrentInstall.AppName).Value;

        if (localAppState == null || string.IsNullOrEmpty(localAppState.Version))
        {
            _logger.Warning("PrepareUpdateTasks: No installed version info, falling back to full install");
            CurrentInstall.Action = ActionType.Install;
            await _downloadManager.InitializeMirrors(
                gameData.BaseUrls,
                _cancellationTokenSource.Token);
            GetChunksToDownload(newManifest);
            return;
        }

        var oldManifestBytes = await _storage.GetCachedManifestBytes(
            CurrentInstall.AppName, localAppState.Version);

        if (oldManifestBytes == null || oldManifestBytes.Length < 1)
        {
            _logger.Warning("PrepareUpdateTasks: Old manifest not cached, falling back to full install");
            CurrentInstall.Action = ActionType.Install;
            await _downloadManager.InitializeMirrors(
                gameData.BaseUrls,
                _cancellationTokenSource.Token);
            GetChunksToDownload(newManifest);
            return;
        }

        var oldManifest = Manifest.ReadAll(oldManifestBytes);
        _logger.Information("PrepareUpdateTasks: Comparing manifests for {AppName}", CurrentInstall.AppName);

        var updatePlan = ManifestUpdatePlanner.Create(
            oldManifest.FileManifestList.Elements,
            newManifest.FileManifestList.Elements);

        _logger.Information(
            "PrepareUpdateTasks: {Unchanged} unchanged, {Changed} changed, {Added} added, {Removed} removed files",
            updatePlan.UnchangedFileCount,
            updatePlan.ChangedFiles.Count,
            updatePlan.AddedFiles.Count,
            updatePlan.RemovedFiles.Count);

        var filesToDownload = updatePlan.ChangedFiles
            .Concat(updatePlan.AddedFiles)
            .ToList();
        foreach (var addedFile in updatePlan.AddedFiles)
        {
            var livePath = ManifestPath.ResolveUnderRoot(CurrentInstall.Location, addedFile.Path);
            if (File.Exists(livePath) || Directory.Exists(livePath))
                throw new IOException($"Update would overwrite an untracked path: {addedFile.Path}");
        }

        var newLocalAppState = BuildInstalledLocalState(
            gameData,
            newManifest,
            localAppState,
            CurrentInstall.Location);
        _updateTransaction = UpdateTransactionState.Create(
            CurrentInstall.Location,
            updatePlan.ChangedFiles.Select(file => file.Path.Value),
            updatePlan.AddedFiles.Select(file => file.Path.Value),
            updatePlan.RemovedFiles.Select(path => path.Value),
            filesToDownload,
            JsonSerializer.Serialize(localAppState),
            JsonSerializer.Serialize(newLocalAppState));
        PrepareUpdateDirectories(_updateTransaction);
        PersistUpdateTransaction(_updateTransaction);

        if (filesToDownload.Count == 0)
        {
            _logger.Information("PrepareUpdateTasks: Update only removes owned files");
            return;
        }

        await _downloadManager.InitializeMirrors(
            gameData.BaseUrls,
            _cancellationTokenSource.Token);
        GetChunksToDownloadFiltered(newManifest, filesToDownload, _updateTransaction.StagingRoot);
    }

    private static LocalAppState BuildInstalledLocalState(
        Game gameData,
        Manifest manifest,
        LocalAppState existing,
        string installPath)
    {
        var state = JsonSerializer.Deserialize<LocalAppState>(JsonSerializer.Serialize(existing))
            ?? new LocalAppState { AppName = gameData.AppName };
        state.AppName = gameData.AppName;
        state.BaseUrls = gameData.BaseUrls;
        state.CanRunOffline = gameData.Metadata?.CustomAttributes?.CanRunOffline?.Value == "true";
        state.Executable = manifest.ManifestMeta.LaunchExe.Value;
        state.InstallPath = installPath;
        state.LaunchParameters = manifest.ManifestMeta.LaunchCommand;
        state.RequiresOt = gameData.Metadata?.CustomAttributes?.OwnershipToken?.Value == "true";
        state.Version = manifest.ManifestMeta.BuildVersion;
        state.Title = gameData.AppTitle;
        if (manifest.ManifestMeta.UninstallActionPath is { } uninstallPath)
        {
            state.Uninstaller = new Dictionary<string, string>
            {
                { uninstallPath.Value, manifest.ManifestMeta.UninstallActionArgs }
            };
        }
        return state;
    }

    private static void PrepareUpdateDirectories(UpdateTransactionState transaction)
    {
        var stagingRoot = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            transaction.StagingRoot);
        var backupRoot = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            transaction.BackupRoot);
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        if (Directory.Exists(backupRoot))
            Directory.Delete(backupRoot, recursive: true);
        Directory.CreateDirectory(stagingRoot);
        _ = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, stagingRoot);
        Directory.CreateDirectory(backupRoot);
        _ = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, backupRoot);
    }

    private async Task CommitPreparedUpdate()
    {
        var transaction = _updateTransaction
            ?? throw new InvalidOperationException("No update transaction is prepared.");
        var invalidFiles = await VerifyFiles(
            transaction.StagingRoot,
            transaction.FilesToVerify,
            _cancellationTokenSource.Token);
        if (invalidFiles.Count > 0)
            throw new InvalidDataException("Staged update files failed verification.");

        transaction.Phase = UpdateTransactionPhase.Committing;
        PersistUpdateTransaction(transaction);
        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.RemovedPaths))
            BackupLiveFile(transaction, relativePath);

        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.AddedPaths))
        {
            UpdatePublicationFaultInjector?.Invoke(relativePath);
            var stagedPath = ManifestPath.ResolveUnderRoot(transaction.StagingRoot, relativePath);
            var livePath = ManifestPath.ResolveUnderRoot(transaction.InstallRoot, relativePath);
            if (!File.Exists(stagedPath))
                throw new InvalidDataException($"Staged update file is missing: {relativePath}");
            if (File.Exists(livePath) || Directory.Exists(livePath))
                throw new IOException($"Update publication target is occupied: {relativePath}");

            EnsureDirectoryExists(livePath);
            stagedPath = ManifestPath.RevalidateUnderRoot(transaction.StagingRoot, stagedPath);
            livePath = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, livePath);
            File.Move(stagedPath, livePath);
            if (!transaction.PublishedPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                transaction.PublishedPaths.Add(relativePath);
            PersistUpdateTransaction(transaction);
        }

        transaction.Phase = UpdateTransactionPhase.Published;
        PersistUpdateTransaction(transaction);
    }

    private void BackupLiveFile(UpdateTransactionState transaction, string relativePath)
    {
        var livePath = ManifestPath.ResolveUnderRoot(transaction.InstallRoot, relativePath);
        if (Directory.Exists(livePath))
            throw new IOException($"Owned update path is a directory: {relativePath}");
        if (!File.Exists(livePath))
            return;

        var backupPath = ManifestPath.ResolveUnderRoot(transaction.BackupRoot, relativePath);
        var backupDirectory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(backupDirectory))
            Directory.CreateDirectory(backupDirectory);
        livePath = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, livePath);
        backupPath = ManifestPath.RevalidateUnderRoot(transaction.BackupRoot, backupPath);
        File.Move(livePath, backupPath);
        if (!transaction.BackedUpPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            transaction.BackedUpPaths.Add(relativePath);
        PersistUpdateTransaction(transaction);
    }

    private void MarkUpdateMetadataCommitted()
    {
        if (_updateTransaction == null)
            return;

        _updateTransaction.Phase = UpdateTransactionPhase.MetadataCommitted;
        PersistUpdateTransaction(_updateTransaction);
    }

    private void CompletePreparedUpdate()
    {
        if (_updateTransaction == null)
            return;

        CleanupUpdateTransaction(_updateTransaction);
        _updateTransaction = null;
    }

    private void RollbackPreparedUpdate()
    {
        if (_updateTransaction == null)
            return;

        RollbackUpdateFiles(_updateTransaction);
        RestoreInstallationState(_updateTransaction, _updateTransaction.OldLocalStateJson);
        CleanupUpdateTransaction(_updateTransaction);
        _updateTransaction = null;
    }

    private void RecoverPendingUpdate(string installRoot)
    {
        var journalPath = ManifestPath.RevalidateUnderRoot(
            installRoot,
            UpdateTransactionState.GetJournalPath(installRoot));
        if (!File.Exists(journalPath))
            return;

        var journal = AtomicJsonFile.ReadAndMigrate(journalPath, UpdateTransactionSchema);
        var transaction = journal.Status switch
        {
            JsonStateReadStatus.Success => journal.Value
                ?? throw new InvalidDataException("Update transaction journal was empty."),
            JsonStateReadStatus.UnsupportedVersion => throw new NotSupportedException(
                $"Update journal schema {journal.Version} is newer than supported schema {UpdateTransactionSchema.CurrentVersion}."),
            _ => throw new InvalidDataException(
                $"Update transaction journal is unavailable: {journal.Status} {journal.Error}.")
        };
        if (!string.Equals(
                Path.GetFullPath(transaction.InstallRoot),
                Path.GetFullPath(installRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update transaction journal root does not match the installation.");

        if (transaction.Phase != UpdateTransactionPhase.MetadataCommitted)
        {
            RollbackUpdateFiles(transaction);
            RestoreInstallationState(transaction, transaction.OldLocalStateJson);
            _logger.Warning("Recovered incomplete update for {InstallRoot} by restoring the old version", installRoot);
        }
        else if (!string.IsNullOrWhiteSpace(transaction.NewLocalStateJson))
        {
            RestoreInstallationState(transaction, transaction.NewLocalStateJson);
        }

        CleanupUpdateTransaction(transaction);
    }

    private void RestoreInstallationState(UpdateTransactionState transaction, string stateJson)
    {
        var state = JsonSerializer.Deserialize<LocalAppState>(stateJson)
            ?? throw new InvalidDataException("Update transaction contains invalid installation state.");
        _storage.AddToLocalAppState(state.AppName, state);
        var game = _libraryManager.GetGameInfo(state.AppName);
        if (game != null)
        {
            game.LocalAppState = state;
            _libraryManager.UpdateGameInfo(game);
        }
    }

    private static void RollbackUpdateFiles(UpdateTransactionState transaction)
    {
        foreach (var relativePath in transaction.AddedPaths)
        {
            var livePath = ManifestPath.ResolveUnderRoot(transaction.InstallRoot, relativePath);
            if (File.Exists(livePath))
            {
                livePath = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, livePath);
                File.Delete(livePath);
            }
        }

        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.RemovedPaths))
        {
            var backupPath = ManifestPath.ResolveUnderRoot(transaction.BackupRoot, relativePath);
            if (!File.Exists(backupPath))
                continue;

            var livePath = ManifestPath.ResolveUnderRoot(transaction.InstallRoot, relativePath);
            if (Directory.Exists(livePath))
                throw new IOException($"Cannot restore update backup over directory: {relativePath}");
            if (File.Exists(livePath))
            {
                livePath = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, livePath);
                File.Delete(livePath);
            }
            var liveDirectory = Path.GetDirectoryName(livePath);
            if (!string.IsNullOrEmpty(liveDirectory))
                Directory.CreateDirectory(liveDirectory);
            backupPath = ManifestPath.RevalidateUnderRoot(transaction.BackupRoot, backupPath);
            livePath = ManifestPath.RevalidateUnderRoot(transaction.InstallRoot, livePath);
            File.Move(backupPath, livePath);
        }
    }

    private void PersistUpdateTransaction(UpdateTransactionState transaction)
    {
        transaction.Revision = checked(transaction.Revision + 1);
        var journalPath = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            UpdateTransactionState.GetJournalPath(transaction.InstallRoot));
        AtomicJsonFile.Write(
            journalPath,
            transaction,
            UpdateTransactionSchema);
        UpdateJournalTransitionFaultInjector?.Invoke(transaction);
    }

    private static void CleanupUpdateTransaction(UpdateTransactionState transaction)
    {
        var stagingRoot = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            transaction.StagingRoot);
        var backupRoot = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            transaction.BackupRoot);
        var journalPath = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            UpdateTransactionState.GetJournalPath(transaction.InstallRoot));
        var journalBackupPath = ManifestPath.RevalidateUnderRoot(
            transaction.InstallRoot,
            journalPath + ".bak");
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);
        if (Directory.Exists(backupRoot))
            Directory.Delete(backupRoot, recursive: true);
        File.Delete(journalPath);
        File.Delete(journalBackupPath);
    }

    /// <summary>
    /// Verify installed files and re-download only broken/missing ones
    /// </summary>
    private async Task PrepareRepairTasks(Game gameData, Manifest manifest)
    {
        _logger.Information("PrepareRepairTasks: Verifying files for {AppName}", CurrentInstall.AppName);

        var invalidFiles = await VerifyFiles(
            CurrentInstall.Location,
            manifest.FileManifestList.Elements,
            _cancellationTokenSource.Token);

        if (invalidFiles.Count == 0)
        {
            _logger.Information("PrepareRepairTasks: All files valid, nothing to repair");
            return;
        }

        _logger.Information("PrepareRepairTasks: {Count} files need repair", invalidFiles.Count);

        await _downloadManager.InitializeMirrors(
            gameData.BaseUrls,
            _cancellationTokenSource.Token);
        GetChunksToDownloadFiltered(manifest, invalidFiles);
    }

    /// <summary>
    /// Queue chunks to download for only a filtered set of file manifests (used by updates)
    /// </summary>
    private void GetChunksToDownloadFiltered(
        Manifest data,
        List<FileManifest> fileManifests,
        string? destinationRoot = null)
    {
        destinationRoot ??= CurrentInstall.Location;
        var addedChunkGuids = new HashSet<BigInteger>();
        var chunkDownloadList = new List<ChunkInfo>();

        foreach (var fileManifest in fileManifests)
        {
            foreach (var chunkPart in fileManifest.ChunkParts)
            {
                if (_chunkToFileManifestsDictionary.TryGetValue(chunkPart.GuidNum, out var existingManifests))
                {
                    existingManifests.Add(fileManifest);
                    _chunkToFileManifestsDictionary[chunkPart.GuidNum] = existingManifests;
                }
                else
                {
                    _chunkToFileManifestsDictionary.TryAdd(chunkPart.GuidNum,
                        new List<FileManifest>() { fileManifest });
                }

                _chunkPartReferences.AddOrUpdate(chunkPart.GuidNum, 1, (key, oldValue) => oldValue + 1);

                if (addedChunkGuids.Contains(chunkPart.GuidNum))
                    continue;

                addedChunkGuids.Add(chunkPart.GuidNum);
                var chunkInfo = data.CDL.GetChunkByGuidNum(chunkPart.GuidNum);
                _downloadQueue.Add(new DownloadTask()
                {
                    Url = chunkInfo.Path,
                    TempPath = Path.Combine(CurrentInstall.Location, ".Crimson", (chunkInfo.GuidNum + ".chunk")),
                    GuidNum = chunkInfo.GuidNum,
                    ChunkInfo = chunkInfo
                });
                chunkDownloadList.Add(chunkInfo);
                CurrentInstall.TotalDownloadSizeBytes += chunkInfo.FileSize;
            }
            CurrentInstall.TotalWriteSizeBytes += fileManifest.FileSize;
        }

        CurrentInstall.TotalDownloadSizeMiB = CurrentInstall.TotalDownloadSizeBytes / 1024.0 / 1024.0;
        CurrentInstall.TotalWriteSizeMb = CurrentInstall.TotalWriteSizeBytes / 1024.0 / 1024.0;

        _logger.Information("GetChunksToDownloadFiltered: Queued {Count} chunks ({SizeMiB:F1} MiB) for {FileCount} files",
            addedChunkGuids.Count, CurrentInstall.TotalDownloadSizeMiB, fileManifests.Count);

        // Create empty files (manifest entries with 0 chunks, e.g. DO_NOT_DELETE.txt)
        foreach (var fileManifest in fileManifests)
        {
            if (fileManifest.ChunkParts.Count == 0)
            {
                var filePath = ManifestPath.ResolveUnderRoot(destinationRoot, fileManifest.Path);
                EnsureDirectoryExists(filePath);
                filePath = ManifestPath.RevalidateUnderRoot(destinationRoot, filePath);
                File.Create(filePath).Dispose();
                _logger.Debug("GetChunksToDownloadFiltered: Created empty file {Path}", filePath);
            }
        }
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

    private async Task HandleInstallationStoppage(string errorMessage, bool userCancellation = false)
    {
        if (CurrentInstall == null)
        {
            _logger.Error("HandleInstallationStoppage called with no active install: {ErrorMessage}", errorMessage);
            ProcessNext();
            return;
        }

        await _cancellationTokenSource.CancelAsync();
        CurrentInstall.Status = ActionStatus.Cancelling;
        InstallationStatusChanged?.Invoke(CurrentInstall);

        if (_downloadTasks != null)
        {
            try { await Task.WhenAll(_downloadTasks); }
            catch (Exception) { }
        }
        if (_installTasks != null)
        {
            try { await Task.WhenAll(_installTasks); }
            catch (Exception) { }
        }

        var cleanupAllowed = true;
        if (_updateTransaction != null)
        {
            try
            {
                RollbackPreparedUpdate();
            }
            catch (Exception rollbackException)
            {
                cleanupAllowed = false;
                _logger.Fatal(rollbackException, "Update rollback failed; preserving recovery journal");
            }
        }

        var tempChunkDir = Path.Combine(CurrentInstall.Location, ".Crimson");
        try
        {
            if (cleanupAllowed && Directory.Exists(tempChunkDir))
                Directory.Delete(tempChunkDir, true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up temp directory {Dir}", tempChunkDir);
        }

        CurrentInstall.Status = userCancellation ? ActionStatus.Cancelled : ActionStatus.Failed;
        _installHistory.Add(CurrentInstall);
        InstallationStatusChanged?.Invoke(CurrentInstall);
        if (userCancellation)
            _logger.Information("Installation cancelled");
        else
            _logger.Error("Installation failed: {ErrorMessage}", errorMessage);

        var state = new InstallManagerState
        {
            CurrentInstall = null,
            IoQueue = null,
            CompletedChunks = null
        };

        try
        {
            var json = JsonSerializer.Serialize(state, _jsonSerializerOptions);
            _storage.SaveInstallState(json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to persist terminal installation state");
        }
        finally
        {
            CompleteCurrentOperation();
            ProcessNext();
        }
    }

    /// <summary>
    ///  Retrieve the Total Size to download and as well as space for install
    /// </summary>
    /// <param name="appName"></param>
    /// <returns></returns>
    public async Task<(double totalDownloadSizeMb, double totalWriteSizeMb)> GetGameDownloadInstallSizes(string appName)
    {
        _logger.Information($"GetGameDownloadInstallSizes: Getting game manifest of {appName}");

        var manifestData = await GetManifestDataWithCaching(appName);

        _logger.Information($"GetGameDownloadInstallSizes: parsing game manifest of {appName}");
        var manifest = Manifest.ReadAll(manifestData);
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

    private async Task<byte[]> GetManifestDataWithCaching(
        string appName,
        CancellationToken cancellationToken = default)
    {
        byte[] manifestData = null;
        var gameData = _libraryManager.GetGameInfo(appName);
        var localAppState = _storage.LocalAppStateDictionary
            .FirstOrDefault(game => game.Key == appName)
            .Value;
        var useCache = localAppState != null && gameData.BaseUrls != null &&
                       gameData.AssetInfos.Windows.BuildVersion == localAppState.CachedManifestVersion;

        if (useCache)
        {
            manifestData = await _storage.GetCachedManifestBytes(
                appName,
                gameData.AssetInfos.Windows.BuildVersion);
        }

        if (manifestData is { Length: > 0 })
        {
            _ = Manifest.Read(manifestData);
            return manifestData;
        }

        var urlResult = await _repository.GetManifestUrls(
            gameData.AssetInfos.Windows.Namespace,
            gameData.AssetInfos.Windows.CatalogItemId,
            gameData.AppName,
            EpicPayloadPlatform.Windows,
            cancellationToken: cancellationToken);
        if (!urlResult.IsSuccess)
            throw new InvalidOperationException(
                $"Manifest metadata request failed: {urlResult.Failure!.Kind}.");

        gameData.BaseUrls = urlResult.Value.BaseUrls;
        _storage.SaveMetaData(gameData);

        var manifestResult = await _repository.GetGameManifest(urlResult.Value, cancellationToken);
        if (!manifestResult.IsSuccess)
            throw new InvalidOperationException(
                $"Manifest request failed: {manifestResult.Failure!.Kind}.");

        manifestData = manifestResult.Value;
        ManifestIntegrity.VerifyDigest(manifestData, urlResult.Value.ManifestHash);
        _ = Manifest.Read(manifestData);
        await _storage.CacheManifestBytes(
            appName,
            gameData.AssetInfos.Windows.BuildVersion,
            manifestData);

        localAppState ??= new LocalAppState
        {
            AppName = appName,
            InstallStatus = InstallState.NotInstalled
        };
        localAppState.CachedManifestVersion = gameData.AssetInfos.Windows.BuildVersion;
        _storage.AddToLocalAppState(appName, localAppState);
        return manifestData;
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
        // Deduplicate: keep only the latest entry per AppName
        var seen = new HashSet<string>();
        var result = new List<string>();
        for (int i = _installHistory.Count - 1; i >= 0; i--)
        {
            if (seen.Add(_installHistory[i].AppName))
                result.Add(_installHistory[i].AppName);
        }
        result.Reverse();
        return result;
    }


    public async Task StopProcessing()
    {
        Task completion;
        CancellationTokenSource cancellationTokenSource = null;
        InstallItem cancellingItem = null;

        lock (_operationLifecycleLock)
        {
            if (CurrentInstall == null)
                return;

            completion = _operationCompletion.Task;
            if (_acceptCancellation &&
                CurrentInstall.Action != ActionType.Import && CurrentInstall.Action != ActionType.Move &&
                _downloadTasks != null && _downloadTasks.All(task => task.IsCompleted) &&
                _installTasks != null && _installTasks.All(task => task.IsCompleted))
            {
                _acceptCancellation = false;
            }

            if (_acceptCancellation)
            {
                _userCancellationRequested = true;
                CurrentInstall.Status = ActionStatus.Cancelling;
                cancellingItem = CurrentInstall;
                cancellationTokenSource = _cancellationTokenSource;
            }
        }

        if (cancellationTokenSource != null)
        {
            InstallationStatusChanged?.Invoke(cancellingItem);
            await cancellationTokenSource.CancelAsync();
        }
        await completion;
    }

    private void BeginFinalization()
    {
        lock (_operationLifecycleLock)
        {
            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            _acceptCancellation = false;
        }
    }

    private void CompleteCurrentOperation()
    {
        TaskCompletionSource completion;
        lock (_operationLifecycleLock)
        {
            completion = _operationCompletion;
            _acceptCancellation = false;
            CurrentInstall = null;
        }
        completion.TrySetResult();
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
        if (CurrentInstall?.Status != ActionStatus.Paused)
        {
            _logger.Warning("No paused installation is available to resume");
            return;
        }

        if (_downloadTasks is null || _installTasks is null)
        {
            ProcessNext(true);
            return;
        }

        _installStopWatch.Start();
        CurrentInstall.Status = ActionStatus.Processing;
        _pauseEvent.Set();
        InstallationStatusChanged?.Invoke(CurrentInstall);
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

internal enum UpdateTransactionPhase
{
    Prepared,
    Committing,
    Published,
    MetadataCommitted
}

internal sealed class UpdateTransactionState
{
    public string InstallRoot { get; set; } = string.Empty;
    public string StagingRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public List<string> ChangedPaths { get; set; } = [];
    public List<string> AddedPaths { get; set; } = [];
    public List<string> RemovedPaths { get; set; } = [];
    public string OldLocalStateJson { get; set; } = string.Empty;
    public UpdateTransactionPhase Phase { get; set; }
    public long Revision { get; set; }
    public List<string> BackedUpPaths { get; set; } = [];
    public List<string> PublishedPaths { get; set; } = [];
    public string NewLocalStateJson { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public List<FileManifest> FilesToVerify { get; set; } = [];

    public static UpdateTransactionState Create(
        string installRoot,
        IEnumerable<string> changedPaths,
        IEnumerable<string> addedPaths,
        IEnumerable<string> removedPaths,
        IEnumerable<FileManifest> filesToVerify,
        string oldLocalStateJson,
        string newLocalStateJson)
    {
        var transactionRoot = Path.Combine(installRoot, ".Crimson");
        return new UpdateTransactionState
        {
            InstallRoot = Path.GetFullPath(installRoot),
            StagingRoot = Path.Combine(transactionRoot, "update-staging"),
            BackupRoot = Path.Combine(transactionRoot, "update-backup"),
            ChangedPaths = changedPaths.ToList(),
            AddedPaths = addedPaths.ToList(),
            RemovedPaths = removedPaths.ToList(),
            FilesToVerify = filesToVerify.ToList(),
            OldLocalStateJson = oldLocalStateJson,
            NewLocalStateJson = newLocalStateJson,
            Phase = UpdateTransactionPhase.Prepared
        };
    }

    public static string GetJournalPath(string installRoot) =>
        Path.Combine(installRoot, ".Crimson", "update-transaction.json");
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
    public long DestinationFileSize { get; set; }
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
