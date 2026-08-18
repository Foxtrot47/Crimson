using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
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
    public event Action<InstallTerminalResult>? OperationCompleted;

    private readonly ILogger _logger;
    private readonly LibraryManager _libraryManager;
    private readonly DownloadManager _downloadManager;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;
    private readonly IInstallFileSystemProbe _fileSystemProbe;

    private readonly List<InstallItem> _installQueue = [];
    private readonly List<InstallItem> _installHistory = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<InstallTerminalResult>> _terminalResults =
        new(StringComparer.Ordinal);

    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private static readonly JsonStateSchema<UpdateTransactionState> UpdateTransactionSchema =
        new("update-transaction", 1, 16L * 1024 * 1024);
    private static readonly JsonStateSchema<InstallTransactionState> InstallTransactionSchema =
        new("install-transaction", 1, 16L * 1024 * 1024);

    private readonly object _installItemLock = new();
    private readonly object _operationLifecycleLock = new();
    private readonly Channel<InstallCommandEnvelope> _commandChannel = Channel.CreateUnbounded<InstallCommandEnvelope>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _commandLoopTask;
    private readonly int _numberOfThreads;
    private const int _progressUpdateIntervalInMS = 1000;
    private InstallOperationContext? _activeContext;
    private Task _processingTask = Task.CompletedTask;

    private InstallOperationContext Operation =>
        _activeContext ?? throw new InvalidOperationException("No install operation is active.");

    private ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary => Operation.FileLocks;
    private ConcurrentDictionary<BigInteger, List<FileManifest>> _chunkToFileManifestsDictionary
    {
        get => Operation.ChunkFiles;
        set => Operation.ChunkFiles = value;
    }
    private ConcurrentDictionary<BigInteger, int> _chunkPartReferences
    {
        get => Operation.ChunkReferences;
        set => Operation.ChunkReferences = value;
    }
    private ConcurrentDictionary<string, byte> _ioQueueTaskSet => Operation.IoTaskSet;
    private List<string> _uninstallManifestPaths => Operation.UninstallManifestPaths;
    private List<FileManifest>? _importVerificationResult
    {
        get => Operation.ImportVerificationResult;
        set => Operation.ImportVerificationResult = value;
    }
    private BlockingCollection<DownloadTask> _downloadQueue
    {
        get => Operation.DownloadQueue;
        set => Operation.DownloadQueue = value;
    }
    private BlockingCollection<IoTask> _ioQueue
    {
        get => Operation.IoQueue;
        set => Operation.IoQueue = value;
    }
    private BlockingCollection<BigInteger> _completedChunks
    {
        get => Operation.CompletedChunks;
        set => Operation.CompletedChunks = value;
    }
    private List<Task>? _downloadTasks
    {
        get => Operation.DownloadWorkers;
        set => Operation.DownloadWorkers = value;
    }
    private List<Task>? _installTasks
    {
        get => Operation.InstallWorkers;
        set => Operation.InstallWorkers = value;
    }
    private CancellationTokenSource _cancellationTokenSource => Operation.Cancellation;
    private Stopwatch _installStopWatch => Operation.Stopwatch;
    private DateTime _lastUpdateTime
    {
        get => Operation.LastProgressUpdate;
        set => Operation.LastProgressUpdate = value;
    }
    private bool _userCancellationRequested
    {
        get => Operation.UserCancellationRequested;
        set => Operation.UserCancellationRequested = value;
    }
    private bool _acceptCancellation
    {
        get => Operation.AcceptCancellation;
        set => Operation.AcceptCancellation = value;
    }
    private TaskCompletionSource _operationCompletion => Operation.Completion;
    private UpdateTransactionState? _updateTransaction
    {
        get => Operation.UpdateTransaction;
        set => Operation.UpdateTransaction = value;
    }
    private InstallTransactionState? _transaction
    {
        get => Operation.Transaction;
        set => Operation.Transaction = value;
    }

    internal Action<string>? UpdatePublicationFaultInjector { get; set; }
    internal Action<UpdateTransactionState>? UpdateJournalTransitionFaultInjector { get; set; }
    internal Action<InstallTransactionState>? InstallJournalWriteFaultInjector { get; set; }
    internal Action<InstallTransactionState>? InstallJournalTransitionFaultInjector { get; set; }

    public InstallItem? CurrentInstall
    {
        get
        {
            lock (_operationLifecycleLock)
                return _activeContext?.Item;
        }
        private set
        {
            lock (_operationLifecycleLock)
            {
                if (value is null)
                {
                    var completed = _activeContext;
                    _activeContext = null;
                    completed?.Dispose();
                    return;
                }

                if (!ReferenceEquals(_activeContext?.Item, value))
                {
                    _activeContext?.Dispose();
                    _activeContext = new InstallOperationContext(value);
                }
            }
        }
    }
    public Task ProcessingTask
    {
        get
        {
            lock (_installItemLock)
                return _processingTask;
        }
    }

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
        _repository = repository;
        _storage = storage;
        _fileSystemProbe = fileSystemProbe;

        _numberOfThreads = 12;
        _jsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new BigIntegerJsonConverter() }
        };
        _commandLoopTask = Task.Run(ProcessCommandsAsync);

        foreach (var installation in _storage.LocalAppStateDictionary.Values)
        {
            if (!string.IsNullOrWhiteSpace(installation.InstallPath))
                RecoverPendingUpdate(installation.InstallPath);
        }
        RecoverPendingOperation();
    }

    public void AddToQueue(InstallItem item) =>
        EnqueueAsync(item).GetAwaiter().GetResult();

    public async Task<InstallCommandResult> EnqueueAsync(
        InstallItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var completion = new TaskCompletionSource<InstallCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _commandChannel.Writer.WriteAsync(
            new InstallCommandEnvelope(InstallCommandKind.Enqueue, item, item.AppName, completion),
            cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    public Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(InstallCommandKind.Pause, cancellationToken: cancellationToken);

    public Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(InstallCommandKind.Resume, cancellationToken: cancellationToken);

    public Task<InstallCommandResult> CancelAsync(
        string appName,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(InstallCommandKind.Cancel, appName, cancellationToken);

    public Task<InstallCommandResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(InstallCommandKind.Shutdown, cancellationToken: cancellationToken);

    public Task<InstallCommandResult> RecoverAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(InstallCommandKind.Recover, cancellationToken: cancellationToken);

    private async Task<InstallCommandResult> SendCommandAsync(
        InstallCommandKind kind,
        string? appName = null,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<InstallCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _commandChannel.Writer.WriteAsync(
            new InstallCommandEnvelope(kind, null, appName, completion),
            cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ProcessCommandsAsync()
    {
        await foreach (var command in _commandChannel.Reader.ReadAllAsync())
        {
            try
            {
                var result = command.Kind switch
                {
                    InstallCommandKind.Enqueue => ExecuteEnqueue(command.Item!),
                    InstallCommandKind.Pause => await ExecutePauseAsync(),
                    InstallCommandKind.Resume => ExecuteResume(),
                    InstallCommandKind.Cancel => await ExecuteCancelAsync(command.AppName),
                    InstallCommandKind.Shutdown => await ExecuteShutdownAsync(),
                    InstallCommandKind.Recover => await ExecuteRecoverAsync(),
                    _ => new InstallCommandResult(
                        InstallCommandOutcome.Rejected,
                        $"Unsupported install command: {command.Kind}.")
                };
                command.Completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Install command {Command} failed", command.Kind);
                command.Completion.TrySetException(exception);
            }
        }
    }

    private InstallCommandResult ExecuteEnqueue(InstallItem item)
    {
        lock (_installItemLock)
        {
            if (_installQueue.Contains(item, new InstallItemComparer()) ||
                string.Equals(CurrentInstall?.AppName, item.AppName, StringComparison.Ordinal))
            {
                _logger.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
                return new InstallCommandResult(
                    InstallCommandOutcome.Rejected,
                    "The game already has an active or queued operation.");
            }
        }

        var gameData = _libraryManager.GetGameInfo(item.AppName);
        if (gameData == null)
        {
            _logger.Warning("AddToQueue: Game {Name} not found in library", item.AppName);
            return new InstallCommandResult(InstallCommandOutcome.NotFound, "The game is not in the library.");
        }

        if (item.Action != ActionType.Install && item.Action != ActionType.Import &&
            (gameData.LocalAppState == null || gameData.LocalAppState.InstallStatus == InstallState.NotInstalled))
        {
            _logger.Warning("AddToQueue: {AppName} is not installed, cannot {Action}", item.AppName, item.Action);
            return new InstallCommandResult(
                InstallCommandOutcome.Rejected,
                "The requested operation requires an installed game.");
        }

        if (item.Action != ActionType.Repair && item.Action != ActionType.Uninstall &&
            gameData.LocalAppState?.InstallStatus == InstallState.Broken)
        {
            _logger.Warning("AddToQueue: {AppName} is broken, forcing repair", item.AppName);
            item.Action = ActionType.Repair;
        }

        var terminal = new TaskCompletionSource<InstallTerminalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_terminalResults.TryAdd(item.AppName, terminal))
            return new InstallCommandResult(
                InstallCommandOutcome.Rejected,
                "The game already has a pending terminal result.");

        _logger.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        lock (_installItemLock)
            _installQueue.Add(item);
        StartProcessingIfIdle();
        return new InstallCommandResult(InstallCommandOutcome.Accepted, Terminal: terminal.Task);
    }

    private void StartProcessingIfIdle()
    {
        lock (_installItemLock)
        {
            if (!_processingTask.IsCompleted)
                return;

            _processingTask = ProcessQueueAsync();
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            lock (_installItemLock)
            {
                if (CurrentInstall is not null || _installQueue.Count == 0)
                    return;
            }

            await ProcessNextAsync();
        }
    }

    private async Task ProcessNextAsync(bool isResuming = false)
    {
        try
        {
            if (!isResuming)
            {
                lock (_installItemLock)
                {
                    if (CurrentInstall is not null || _installQueue.Count == 0)
                        return;
                    CurrentInstall = _installQueue[0];
                    _installQueue.RemoveAt(0);
                }

                lock (_operationLifecycleLock)
                    _acceptCancellation = true;
                await PrepareTasks();
            }
            else
            {
                await PrepareTasks(true, Operation.ResumeCompletedChunks);
            }

            // PrepareTasks may call HandleInstallationStoppage (e.g. import folder empty),
            // which sets CurrentInstall = null. Bail out if that happened.
            if (CurrentInstall == null) return;

            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            CurrentInstall.Status = ActionStatus.Processing;
            InstallationStatusChanged?.Invoke(CurrentInstall);

            // Import does not mutate the live tree. Move commits without worker queues.
            if (CurrentInstall.Action is ActionType.Import or ActionType.Move)
            {
                if (CurrentInstall.Action == ActionType.Move)
                    await CommitPreparedTransaction();
                await UpdateInstalledGameStatus();
                return;
            }

            _installStopWatch.Start();

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
            else if (_transaction is not null)
                await CommitPreparedTransaction();
            await UpdateInstalledGameStatus();

        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            if (Operation.PauseRequested)
                return;
            var workerFailure = Operation.WorkerFailure;
            await HandleInstallationStoppage(
                _userCancellationRequested
                    ? "Installation cancelled"
                    : workerFailure is null
                        ? "Installation worker cancelled"
                        : $"{workerFailure.GetType().Name}: {workerFailure.Message}",
                _userCancellationRequested);
        }
        catch (InstallProcessTerminationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is InstallPlanningException planningException)
                Operation.PlanningFailure = planningException.Failure;
            _logger.Error(ex, "ProcessNext: Installation failed");
            await HandleInstallationStoppage(
                $"{ex.GetType().Name}: {ex.Message}",
                _userCancellationRequested);
        }
    }

    private async Task PrepareTasks(bool isResuming = false, List<BigInteger> downloadedChunks = null)
    {
        try
        {
            if (!isResuming && CurrentInstall is null)
                return;

            if (CurrentInstall == null) return;
            _logger.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);

            var manifestData = await GetManifestDataWithCaching(
                CurrentInstall.AppName,
                _cancellationTokenSource.Token);
            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);

            _logger.Information("ProcessNext: Parsing game manifest");
            var data = Manifest.ReadAll(manifestData);

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

            if (CurrentInstall.Action is ActionType.Install or ActionType.Uninstall or ActionType.Import)
            {
                CreateAndPersistPlan(data, manifestData, writeProbe);
                if (CurrentInstall.Action != ActionType.Import)
                    PrepareTransactionDirectories();
            }

            if (CurrentInstall.Action == ActionType.Install)
            {
                await _downloadManager.InitializeMirrors(
                    gameData.BaseUrls,
                    _cancellationTokenSource.Token);
                GetChunksToDownloadFiltered(
                    data,
                    GetPendingManifestFiles(data),
                    _transaction!.StagingRoot);
            }
            else if (CurrentInstall.Action == ActionType.Update)
            {
                await PrepareUpdateTasks(gameData, data, manifestData);
            }
            else if (CurrentInstall.Action == ActionType.Repair)
            {
                await PrepareRepairTasks(gameData, data, manifestData, writeProbe);
            }
            else if (CurrentInstall.Action == ActionType.Uninstall)
            {
                foreach (var fileManifest in data.FileManifestList.Elements)
                {
                    _uninstallManifestPaths.Add(fileManifest.Path.Value);
                    CurrentInstall.TotalWriteSizeMb += fileManifest.FileSize / 1024.0 / 1024.0;
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
                var destinationParent = Path.GetDirectoryName(Path.GetFullPath(CurrentInstall.MoveLocation));
                if (string.IsNullOrWhiteSpace(destinationParent))
                {
                    await HandleInstallationStoppage("Move destination has no parent directory");
                    return;
                }
                var destinationProbe = _fileSystemProbe.Probe(destinationParent);
                if (Directory.Exists(CurrentInstall.MoveLocation))
                {
                    await HandleInstallationStoppage("Destination directory already exists");
                    return;
                }

                CreateAndPersistPlan(
                    data,
                    manifestData,
                    destinationProbe,
                    moveDestination: CurrentInstall.MoveLocation,
                    source: writeProbe);
                PrepareTransactionDirectories();
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

    private InstallOperationPlan CreateAndPersistPlan(
        Manifest manifest,
        byte[] manifestBytes,
        InstallFileSystemProbeResult destination,
        IReadOnlyList<InstallPlanFile>? installedFiles = null,
        IReadOnlyCollection<string>? invalidFiles = null,
        string? moveDestination = null,
        InstallFileSystemProbeResult? source = null)
    {
        var local = _storage.LocalAppStateDictionary
            .FirstOrDefault(pair => pair.Key == CurrentInstall!.AppName).Value;
        var targetIdentity = new InstallManifestIdentity(
            manifest.ManifestMeta.BuildVersion,
            ComputeManifestSha1(manifestBytes),
            ComputeManifestSha256(manifestBytes));
        var targetFiles = manifest.FileManifestList.Elements
            .Select(ToPlanFile)
            .ToArray();
        var installedIdentity = local is null
            ? null
            : new InstallManifestIdentity(
                local.InstalledManifestBuildVersion ?? local.Version ?? string.Empty,
                local.InstalledManifestSha1 ?? string.Empty,
                local.InstalledManifestSha256 ?? string.Empty);
        var verifiedStagedFiles = Operation.Plan is { } previousPlan &&
            string.Equals(
                previousPlan.TargetManifest.Sha256,
                targetIdentity.Sha256,
                StringComparison.OrdinalIgnoreCase)
                ? previousPlan.VerifiedStageFiles.Select(file => file.Path).ToArray()
                : [];
        var result = InstallOperationPlanner.Create(new InstallPlanningRequest(
            Operation.OperationId,
            CurrentInstall!.AppName,
            CurrentInstall.Action,
            Path.GetFullPath(CurrentInstall.Location),
            targetIdentity,
            targetFiles,
            destination,
            installedIdentity,
            installedFiles ?? (CurrentInstall.Action == ActionType.Install ? [] : targetFiles),
            invalidFiles,
            verifiedStagedFiles,
            MoveDestination: moveDestination,
            Source: source));
        if (!result.IsSuccess)
            throw new InstallPlanningException(result.Failure!.Value, result.Message!);

        Operation.Plan = result.Plan!;
        _transaction = InstallTransactionState.Create(
            result.Plan!,
            local is null ? null : JsonSerializer.Serialize(local),
            null);
        PersistDurableOperationState();
        return result.Plan!;
    }

    private static InstallPlanFile ToPlanFile(FileManifest file) => new(
        file.Path.Value,
        file.FileSize,
        Convert.ToHexString(file.ShaHash).ToLowerInvariant());

    private List<FileManifest> GetPendingManifestFiles(Manifest manifest)
    {
        var pending = new HashSet<string>(
            Operation.Plan?.PendingStageFiles.Select(file => file.Path) ?? [],
            StringComparer.OrdinalIgnoreCase);
        return manifest.FileManifestList.Elements
            .Where(file => pending.Contains(file.Path.Value))
            .ToList();
    }

    private void PersistDurableOperationState()
    {
        var state = new InstallManagerState
        {
            CurrentInstall = CurrentInstall,
            IoQueue = [],
            CompletedChunks = [.. Operation.ResumeCompletedChunks, .. _completedChunks],
            Plan = Operation.Plan,
            Phase = _transaction?.Phase
        };
        _storage.SaveInstallState(JsonSerializer.Serialize(state, _jsonSerializerOptions));
    }

    private void PrepareTransactionDirectories()
    {
        var transaction = _transaction
            ?? throw new InvalidOperationException("No install transaction is planned.");
        Directory.CreateDirectory(transaction.Plan.InstallRoot);
        _ = ManifestPath.RevalidateUnderRoot(transaction.Plan.InstallRoot, transaction.Plan.InstallRoot);
        foreach (var path in new[] { transaction.StagingRoot, transaction.BackupRoot, transaction.TrashRoot })
        {
            Directory.CreateDirectory(path);
            _ = ManifestPath.RevalidateUnderRoot(transaction.Plan.InstallRoot, path);
        }
        transaction.Phase = InstallTransactionPhase.Staging;
        PersistTransaction(transaction);
    }

    private void PersistTransaction(InstallTransactionState transaction)
    {
        transaction.Revision = checked(transaction.Revision + 1);
        var journalPath = transaction.Plan.Action == ActionType.Move &&
            !Directory.Exists(transaction.Plan.InstallRoot)
                ? Path.Combine(
                    transaction.Plan.MoveDestination!,
                    ".Crimson",
                    "operations",
                    transaction.Plan.OperationId,
                    "journal.json")
                : transaction.JournalPath;
        InstallJournalWriteFaultInjector?.Invoke(transaction);
        AtomicJsonFile.Write(journalPath, transaction, InstallTransactionSchema);
        PersistDurableOperationState();
        InstallJournalTransitionFaultInjector?.Invoke(transaction);
    }

    private void ThrowIfRecoveryRequested()
    {
        if (Operation.RecoveryRequested)
            throw new InvalidOperationException("Cancellation requested transactional recovery.");
    }

    private async Task CommitPreparedTransaction()
    {
        var transaction = _transaction
            ?? throw new InvalidOperationException("No install transaction is planned.");
        if (transaction.Plan.Action is ActionType.Import or ActionType.Update)
            return;

        if (transaction.Plan.Action is ActionType.Install or ActionType.Repair)
            await VerifyStagedFiles(transaction);
        transaction.Phase = InstallTransactionPhase.ReadyToCommit;
        PersistTransaction(transaction);
        BeginFinalization();
        transaction.Phase = InstallTransactionPhase.Committing;
        PersistTransaction(transaction);
        ThrowIfRecoveryRequested();

        switch (transaction.Plan.Action)
        {
            case ActionType.Install:
            case ActionType.Repair:
                foreach (var file in transaction.Plan.VerifiedStageFiles.Concat(transaction.Plan.PendingStageFiles))
                {
                    ThrowIfRecoveryRequested();
                    var stagedPath = ManifestPath.ResolveUnderRoot(transaction.StagingRoot, file.Path);
                    var livePath = ManifestPath.ResolveUnderRoot(transaction.Plan.InstallRoot, file.Path);
                    if (!File.Exists(stagedPath))
                        throw new InvalidDataException($"Staged file is missing: {file.Path}");
                    if (!string.Equals(
                            Util.CalculateSHA1(stagedPath),
                            file.Sha1,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Staged file changed before publication: {file.Path}");
                    }
                    if (File.Exists(livePath))
                    {
                        if (transaction.Plan.Action == ActionType.Install)
                            throw new IOException($"Install would overwrite an existing path: {file.Path}");
                        var backupPath = ManifestPath.ResolveUnderRoot(transaction.BackupRoot, file.Path);
                        EnsureDirectoryExists(backupPath);
                        File.Move(livePath, backupPath);
                        transaction.BackedUpPaths.Add(file.Path);
                        PersistTransaction(transaction);
                        ThrowIfRecoveryRequested();
                    }
                    else if (Directory.Exists(livePath))
                    {
                        throw new IOException($"Publication target is a directory: {file.Path}");
                    }

                    EnsureDirectoryExists(livePath);
                    File.Move(stagedPath, livePath);
                    transaction.PublishedPaths.Add(file.Path);
                    PersistTransaction(transaction);
                    ThrowIfRecoveryRequested();
                }
                break;

            case ActionType.Uninstall:
                foreach (var relativePath in transaction.Plan.RemoveFiles)
                {
                    ThrowIfRecoveryRequested();
                    var livePath = ManifestPath.ResolveUnderRoot(transaction.Plan.InstallRoot, relativePath);
                    if (!File.Exists(livePath))
                        continue;
                    var trashPath = ManifestPath.ResolveUnderRoot(transaction.TrashRoot, relativePath);
                    EnsureDirectoryExists(trashPath);
                    File.Move(livePath, trashPath);
                    transaction.TrashedPaths.Add(relativePath);
                    PersistTransaction(transaction);
                    ThrowIfRecoveryRequested();
                }
                break;

            case ActionType.Move:
                ThrowIfRecoveryRequested();
                var destination = transaction.Plan.MoveDestination
                    ?? throw new InvalidOperationException("Move transaction has no destination.");
                Directory.Move(transaction.Plan.InstallRoot, destination);
                ThrowIfRecoveryRequested();
                break;
        }

        ThrowIfRecoveryRequested();
        transaction.Phase = InstallTransactionPhase.Published;
        PersistTransaction(transaction);
    }

    private async Task VerifyStagedFiles(InstallTransactionState transaction)
    {
        foreach (var file in transaction.Plan.VerifiedStageFiles.Concat(transaction.Plan.PendingStageFiles))
        {
            _cancellationTokenSource.Token.ThrowIfCancellationRequested();
            var stagedPath = ManifestPath.ResolveUnderRoot(transaction.StagingRoot, file.Path);
            if (!File.Exists(stagedPath))
                throw new InvalidDataException($"Staged file is missing: {file.Path}");
            var actual = await Task.Run(() => Util.CalculateSHA1(stagedPath), _cancellationTokenSource.Token);
            if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged file hash mismatch: {file.Path}");
        }
    }

    private void MarkTransactionMetadataCommitted()
    {
        if (_transaction is null)
            return;
        _transaction.Phase = InstallTransactionPhase.MetadataCommitted;
        if (_transaction.Plan.Action == ActionType.Move &&
            !Directory.Exists(_transaction.Plan.InstallRoot))
        {
            PersistDurableOperationState();
            return;
        }
        PersistTransaction(_transaction);
    }

    private void CompletePreparedTransaction()
    {
        if (_transaction is null)
            return;
        _transaction.Phase = InstallTransactionPhase.Completed;
        if (_transaction.Plan.Action == ActionType.Move &&
            !Directory.Exists(_transaction.Plan.InstallRoot))
        {
            PersistDurableOperationState();
        }
        else
        {
            PersistTransaction(_transaction);
        }

        var operationRoot = _transaction.Plan.Action == ActionType.Move
            ? Path.Combine(
                _transaction.Plan.MoveDestination!,
                ".Crimson",
                "operations",
                _transaction.Plan.OperationId)
            : Path.GetDirectoryName(_transaction.JournalPath)!;
        if (Directory.Exists(operationRoot))
            Directory.Delete(operationRoot, recursive: true);
        _transaction = null;
        PersistDurableOperationState();
    }

    private void RollbackPreparedTransaction()
    {
        if (_transaction is null)
            return;
        var transaction = _transaction;
        if (transaction.Phase == InstallTransactionPhase.Completed)
        {
            var completedRoot = transaction.Plan.Action == ActionType.Move
                ? Path.Combine(
                    transaction.Plan.MoveDestination!,
                    ".Crimson",
                    "operations",
                    transaction.Plan.OperationId)
                : Path.GetDirectoryName(transaction.JournalPath)!;
            if (Directory.Exists(completedRoot))
                Directory.Delete(completedRoot, recursive: true);
            _transaction = null;
            PersistDurableOperationState();
            return;
        }
        if (transaction.Phase == InstallTransactionPhase.MetadataCommitted)
        {
            CompletePreparedTransaction();
            return;
        }

        if (transaction.Plan.Action == ActionType.Move)
        {
            var destination = transaction.Plan.MoveDestination!;
            if (!Directory.Exists(transaction.Plan.InstallRoot) && Directory.Exists(destination))
                Directory.Move(destination, transaction.Plan.InstallRoot);
        }
        else
        {
            foreach (var relativePath in transaction.PublishedPaths.AsEnumerable().Reverse())
            {
                var livePath = ManifestPath.ResolveUnderRoot(transaction.Plan.InstallRoot, relativePath);
                if (File.Exists(livePath))
                    File.Delete(livePath);
            }
            foreach (var relativePath in transaction.BackedUpPaths.AsEnumerable().Reverse())
            {
                var backupPath = ManifestPath.ResolveUnderRoot(transaction.BackupRoot, relativePath);
                if (!File.Exists(backupPath))
                    continue;
                var livePath = ManifestPath.ResolveUnderRoot(transaction.Plan.InstallRoot, relativePath);
                EnsureDirectoryExists(livePath);
                File.Move(backupPath, livePath, overwrite: true);
            }
            foreach (var relativePath in transaction.TrashedPaths.AsEnumerable().Reverse())
            {
                var trashPath = ManifestPath.ResolveUnderRoot(transaction.TrashRoot, relativePath);
                if (!File.Exists(trashPath))
                    continue;
                var livePath = ManifestPath.ResolveUnderRoot(transaction.Plan.InstallRoot, relativePath);
                EnsureDirectoryExists(livePath);
                File.Move(trashPath, livePath, overwrite: true);
            }
        }

        var operationRoot = Path.GetDirectoryName(transaction.JournalPath)!;
        if (Directory.Exists(operationRoot))
            Directory.Delete(operationRoot, recursive: true);
        _transaction = null;
        PersistDurableOperationState();
    }

    private void RecoverPendingOperation()
    {
        InstallManagerState? state;
        try
        {
            state = JsonSerializer.Deserialize<InstallManagerState>(
                _storage.GetInstallState(),
                _jsonSerializerOptions);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Durable install operation state could not be read");
            return;
        }

        if (state?.Plan is null || state.Phase is null)
            return;
        if (state.Phase == InstallTransactionPhase.Paused)
            return;

        try
        {
            var item = state.CurrentInstall ?? new InstallItem(
                state.Plan.AppName,
                state.Plan.Action,
                state.Plan.InstallRoot)
            {
                MoveLocation = state.Plan.MoveDestination
            };
            lock (_operationLifecycleLock)
                _activeContext = new InstallOperationContext(item, state.Plan.OperationId);
            Operation.Plan = state.Plan;
            Operation.ResumeCompletedChunks.AddRange(state.CompletedChunks ?? []);

            var planned = InstallTransactionState.Create(state.Plan, null, null);
            var journalPath = planned.JournalPath;
            if (!File.Exists(journalPath) && state.Plan.Action == ActionType.Move &&
                !string.IsNullOrWhiteSpace(state.Plan.MoveDestination))
            {
                journalPath = Path.Combine(
                    state.Plan.MoveDestination,
                    ".Crimson",
                    "operations",
                    state.Plan.OperationId,
                    "journal.json");
            }

            if (File.Exists(journalPath))
            {
                var read = AtomicJsonFile.ReadAndMigrate(journalPath, InstallTransactionSchema);
                if (!read.IsSuccess || read.Value is null)
                    throw new InvalidDataException($"Install transaction journal is unavailable: {read.Status}.");
                _transaction = read.Value;
            }
            else
            {
                planned.Phase = state.Phase.Value;
                _transaction = planned;
            }

            RollbackPreparedTransaction();
            _storage.SaveInstallState(JsonSerializer.Serialize(new InstallManagerState(), _jsonSerializerOptions));
            CurrentInstall = null;
        }
        catch (Exception exception)
        {
            _logger.Fatal(exception, "Install transaction recovery failed; launch remains blocked");
            if (_activeContext is not null)
            {
                Operation.Transaction ??= InstallTransactionState.Create(state.Plan, null, null);
                Operation.Transaction.Phase = InstallTransactionPhase.RecoveryRequired;
                PersistDurableOperationState();
            }
        }
    }

    private async Task ProcessDownloadQueue()
    {
        var context = Operation;
        var cancellation = context.Cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            foreach (var downloadTask in context.DownloadQueue.GetConsumingEnumerable(cancellationToken))
            {
                _logger.Debug(
                    "ProcessDownloadQueue: Downloading chunk with guid{Guid} from {Url} to {Path}",
                    downloadTask.GuidNum,
                    downloadTask.Url,
                    downloadTask.TempPath);
                var success = await _downloadManager.DownloadFileWithFallback(
                    downloadTask.Url,
                    downloadTask.TempPath,
                    cancellationToken: cancellationToken,
                    expectedSize: downloadTask.ChunkInfo.FileSize);
                if (!success)
                    throw new IOException($"Failed to download chunk {downloadTask.GuidNum} from all mirrors");

                var downloadedChunkBytes = await File.ReadAllBytesAsync(
                    downloadTask.TempPath,
                    cancellationToken);
                var downloadedChunk = Chunk.ReadBuffer(downloadedChunkBytes);
                downloadedChunk.ValidateAgainst(downloadTask.ChunkInfo);
                UpdateDownloadProgress(downloadTask.ChunkInfo.FileSize);
                CreateIoTasksForChunk(downloadTask);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WorkerFailure = ex;
            _logger.Error(ex, "ProcessDownloadQueue: Download worker failed");
            cancellation.Cancel();
            throw;
        }
    }

    private void CreateIoTasksForChunk(DownloadTask downloadTask)
    {
        // get file manifest from dictionary
        var writeRoot = _transaction?.StagingRoot ?? _updateTransaction?.StagingRoot ?? CurrentInstall.Location;
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
        var context = Operation;
        var cancellation = context.Cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            foreach (var ioTask in context.IoQueue.GetConsumingEnumerable(cancellationToken))
            {
                switch (ioTask.TaskType)
                {
                    case IoTaskType.Copy:
                        await ProcessCopyTask(ioTask, cancellationToken);
                        break;
                    case IoTaskType.Delete:
                        cancellationToken.ThrowIfCancellationRequested();
                        var deletePath = ManifestPath.RevalidateUnderRoot(
                            context.Item.Location,
                            ioTask.DestinationFilePath);
                        File.Delete(deletePath);
                        break;
                }
                UpdateInstallWriteProgress(ioTask.Size);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WorkerFailure = ex;
            _logger.Error(ex, "ProcessIoQueue: IO worker failed");
            cancellation.Cancel();
            throw;
        }
    }

    private async Task ProcessCopyTask(IoTask ioTask, CancellationToken cancellationToken)
    {
        var writeRoot = _transaction?.StagingRoot ?? _updateTransaction?.StagingRoot ?? CurrentInstall.Location;
        var destinationPath = ManifestPath.RevalidateUnderRoot(
            writeRoot,
            ioTask.DestinationFilePath);
        EnsureDirectoryExists(destinationPath);
        destinationPath = ManifestPath.RevalidateUnderRoot(writeRoot, destinationPath);

        var fileLock = _fileLocksConcurrentDictionary.GetOrAdd(destinationPath, new object());
        var compressedChunkData = await File.ReadAllBytesAsync(ioTask.SourceFilePath, cancellationToken);
        var chunk = Chunk.ReadBuffer(compressedChunkData);
        _logger.Debug("ProcessIoQueue: Reading chunk buffers from {Source} finished", ioTask.SourceFilePath);

        if (ioTask.Offset < 0 || ioTask.Size < 0 || ioTask.FileOffset < 0 || ioTask.DestinationFileSize < 0 ||
            ioTask.Offset > chunk.Data.LongLength || ioTask.Size > chunk.Data.LongLength - ioTask.Offset ||
            ioTask.FileOffset > ioTask.DestinationFileSize ||
            ioTask.Size > ioTask.DestinationFileSize - ioTask.FileOffset)
        {
            throw new InvalidDataException($"Invalid chunk range for {destinationPath}");
        }

        lock (fileLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fileStream = new FileStream(
                destinationPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.None);
            fileStream.SetLength(ioTask.DestinationFileSize);
            fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);

            using var memoryStream = new MemoryStream(chunk.Data);
            memoryStream.Seek(ioTask.Offset, SeekOrigin.Begin);
            var remainingBytesToWrite = ioTask.Size;
            const int bufferSize = 4096;
            var buffer = new byte[bufferSize];
            while (remainingBytesToWrite > 0)
            {
                var bytesToRead = (int)Math.Min(bufferSize, remainingBytesToWrite);
                cancellationToken.ThrowIfCancellationRequested();
                memoryStream.ReadExactly(buffer, 0, bytesToRead);
                fileStream.Write(buffer, 0, bytesToRead);
                remainingBytesToWrite -= bytesToRead;
            }
            fileStream.Flush(flushToDisk: true);
        }

        var newCount = _chunkPartReferences.AddOrUpdate(
            ioTask.GuidNum,
            _ => 0,
            (_, oldValue) => oldValue - 1);
        if (newCount <= 0 && _chunkPartReferences.TryRemove(ioTask.GuidNum, out _))
        {
            _completedChunks.Add(ioTask.SourceChunkGuidNum);
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
                    localAppState.InstalledManifestBuildVersion = manifestData.ManifestMeta.BuildVersion;
                    localAppState.InstalledManifestSha1 = ComputeManifestSha1(manifestBytes);
                    localAppState.InstalledManifestSha256 = ComputeManifestSha256(manifestBytes);
                    localAppState.AvailableManifestDigest = localAppState.InstalledManifestSha256;
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
                        CurrentInstall.Location,
                        ComputeManifestSha1(manifestBytes),
                        ComputeManifestSha256(manifestBytes));
                    if (invalidFilesList.Count > 0)
                    {
                        _logger.Warning(
                            "UpdateInstalledGameStatus: {Count} files failed verification for {AppName}. Marking as Broken.",
                            invalidFilesList.Count,
                            CurrentInstall.AppName);
                        localAppState.InstallStatus = InstallState.Broken;
                    }
                    else
                    {
                        _logger.Information(
                            "UpdateInstalledGameStatus: Verification successful for {AppName}",
                            CurrentInstall.AppName);
                        localAppState.InstallStatus = InstallState.Installed;
                    }

                    gameData.LocalAppState = localAppState;
                    _storage.AddToLocalAppState(gameData.AppName, localAppState);
                    _libraryManager.UpdateGameInfo(gameData);
                    break;
                }
            }

            MarkTransactionMetadataCommitted();
            if (CurrentInstall.Action == ActionType.Update && _updateTransaction != null)
            {
                MarkUpdateMetadataCommitted();
                CompletePreparedUpdate();
            }
            CompletePreparedTransaction();

            CurrentInstall.Status = ActionStatus.Success;
            PublishTerminal(CurrentInstall, InstallTerminalOutcome.Succeeded);
            CompleteCurrentOperation();
            return;
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (InstallProcessTerminationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Fatal("UpdateInstalledGameStatus: Exception {Exception}", ex);

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

            if (_transaction != null)
            {
                try
                {
                    RollbackPreparedTransaction();
                }
                catch (Exception rollbackException)
                {
                    _transaction.Phase = InstallTransactionPhase.RecoveryRequired;
                    PersistDurableOperationState();
                    _logger.Fatal(rollbackException, "Operation rollback failed; startup recovery is required");
                }
            }

            if (CurrentInstall != null)
            {
                CurrentInstall.Status = ActionStatus.Failed;
                PublishTerminal(CurrentInstall, InstallTerminalOutcome.Failed, ex.GetType().Name);
            }
            CompleteCurrentOperation();
            return;
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

                    if (!File.Exists(filePath))
                    {
                        _logger.Warning("VerifyFiles: MISSING {Filename}", manifest.Path);
                        invalidFilesBag.Add(manifest);
                        return;
                    }

                    filePath = ManifestPath.RevalidateUnderRoot(installPath, filePath);
                    var fileSha1 = Util.CalculateSHA1(filePath);
                    token.ThrowIfCancellationRequested();
                    var expectedHash = BitConverter.ToString(manifest.ShaHash).Replace("-", "").ToLowerInvariant();
                    if (fileSha1 != expectedHash)
                    {
                        var fileInfo = new FileInfo(filePath);
                        _logger.Warning(
                            "VerifyFiles: HASH MISMATCH {Filename} (size={Size}, expected={Expected}, actual={Actual})",
                            manifest.Path,
                            fileInfo.Length,
                            expectedHash,
                            fileSha1);
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
    /// Prepare update tasks by comparing old and new manifests.
    /// Downloads only chunks for changed/added files and deletes removed files.
    /// Falls back to full reinstall if old manifest is unavailable.
    /// </summary>
    private async Task PrepareUpdateTasks(
        Game gameData,
        Manifest newManifest,
        byte[] newManifestBytes)
    {
        var newManifestSha1 = ComputeManifestSha1(newManifestBytes);
        var newManifestSha256 = ComputeManifestSha256(newManifestBytes);
        // Try to load the old manifest for the currently installed version
        var localAppState = _storage.LocalAppStateDictionary
            .FirstOrDefault(g => g.Key == CurrentInstall.AppName).Value;

        if (localAppState == null || string.IsNullOrEmpty(localAppState.Version))
        {
            _logger.Warning("PrepareUpdateTasks: No installed version info, falling back to full install");
            CurrentInstall.Action = ActionType.Install;
            CreateAndPersistPlan(
                newManifest,
                newManifestBytes,
                _fileSystemProbe.Probe(CurrentInstall.Location));
            PrepareTransactionDirectories();
            await _downloadManager.InitializeMirrors(
                gameData.BaseUrls,
                _cancellationTokenSource.Token);
            GetChunksToDownloadFiltered(
                newManifest,
                GetPendingManifestFiles(newManifest),
                _transaction!.StagingRoot);
            return;
        }

        var oldManifestBytes = await _storage.GetCachedManifestBytes(
            CurrentInstall.AppName, localAppState.Version);

        if (oldManifestBytes == null || oldManifestBytes.Length < 1)
        {
            _logger.Warning("PrepareUpdateTasks: Old manifest not cached, falling back to full install");
            CurrentInstall.Action = ActionType.Install;
            CreateAndPersistPlan(
                newManifest,
                newManifestBytes,
                _fileSystemProbe.Probe(CurrentInstall.Location));
            PrepareTransactionDirectories();
            await _downloadManager.InitializeMirrors(
                gameData.BaseUrls,
                _cancellationTokenSource.Token);
            GetChunksToDownloadFiltered(
                newManifest,
                GetPendingManifestFiles(newManifest),
                _transaction!.StagingRoot);
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

        CreateAndPersistPlan(
            newManifest,
            newManifestBytes,
            _fileSystemProbe.Probe(CurrentInstall.Location),
            oldManifest.FileManifestList.Elements.Select(ToPlanFile).ToArray());
        PrepareTransactionDirectories();
        var pendingPaths = new HashSet<string>(
            Operation.Plan!.PendingStageFiles.Select(file => file.Path),
            StringComparer.OrdinalIgnoreCase);
        var pendingFilesToDownload = filesToDownload
            .Where(file => pendingPaths.Contains(file.Path.Value))
            .ToList();

        var newLocalAppState = BuildInstalledLocalState(
            gameData,
            newManifest,
            localAppState,
            CurrentInstall.Location,
            newManifestSha1,
            newManifestSha256);
        _transaction!.NewLocalStateJson = JsonSerializer.Serialize(newLocalAppState);
        _updateTransaction = UpdateTransactionState.Create(
            CurrentInstall.Location,
            updatePlan.ChangedFiles.Select(file => file.Path.Value),
            updatePlan.AddedFiles.Select(file => file.Path.Value),
            updatePlan.RemovedFiles.Select(path => path.Value),
            filesToDownload,
            JsonSerializer.Serialize(localAppState),
            JsonSerializer.Serialize(newLocalAppState));
        _updateTransaction.StagingRoot = _transaction!.StagingRoot;
        _updateTransaction.BackupRoot = _transaction.BackupRoot;
        PersistUpdateTransaction(_updateTransaction);

        if (filesToDownload.Count == 0)
        {
            _logger.Information("PrepareUpdateTasks: Update only removes owned files");
            return;
        }

        await _downloadManager.InitializeMirrors(
            gameData.BaseUrls,
            _cancellationTokenSource.Token);
        GetChunksToDownloadFiltered(
            newManifest,
            pendingFilesToDownload,
            _updateTransaction.StagingRoot);
    }

    private static LocalAppState BuildInstalledLocalState(
        Game gameData,
        Manifest manifest,
        LocalAppState existing,
        string installPath,
        string manifestSha1,
        string manifestSha256)
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
        state.InstalledManifestBuildVersion = manifest.ManifestMeta.BuildVersion;
        state.InstalledManifestSha1 = manifestSha1;
        state.InstalledManifestSha256 = manifestSha256;
        state.AvailableManifestDigest = manifestSha256;
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

    private static string ComputeManifestSha1(byte[] manifestBytes) =>
        Convert.ToHexString(SHA1.HashData(manifestBytes)).ToLowerInvariant();

    private static string ComputeManifestSha256(byte[] manifestBytes) =>
        Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

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
        {
            ThrowIfRecoveryRequested();
            BackupLiveFile(transaction, relativePath);
            ThrowIfRecoveryRequested();
        }

        foreach (var relativePath in transaction.ChangedPaths.Concat(transaction.AddedPaths))
        {
            ThrowIfRecoveryRequested();
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
            ThrowIfRecoveryRequested();
        }

        ThrowIfRecoveryRequested();
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
    private async Task PrepareRepairTasks(
        Game gameData,
        Manifest manifest,
        byte[] manifestBytes,
        InstallFileSystemProbeResult writeProbe)
    {
        _logger.Information("PrepareRepairTasks: Verifying files for {AppName}", CurrentInstall!.AppName);
        var invalidFiles = await VerifyFiles(
            CurrentInstall.Location,
            manifest.FileManifestList.Elements,
            _cancellationTokenSource.Token);

        CreateAndPersistPlan(
            manifest,
            manifestBytes,
            writeProbe,
            invalidFiles: invalidFiles.Select(file => file.Path.Value).ToArray());
        PrepareTransactionDirectories();
        if (invalidFiles.Count == 0)
        {
            _logger.Information("PrepareRepairTasks: All files valid, nothing to repair");
            return;
        }

        _logger.Information("PrepareRepairTasks: {Count} files need repair", invalidFiles.Count);
        await _downloadManager.InitializeMirrors(
            gameData.BaseUrls,
            _cancellationTokenSource.Token);
        var pendingPaths = new HashSet<string>(
            Operation.Plan!.PendingStageFiles.Select(file => file.Path),
            StringComparer.OrdinalIgnoreCase);
        GetChunksToDownloadFiltered(
            manifest,
            invalidFiles.Where(file => pendingPaths.Contains(file.Path.Value)).ToList(),
            _transaction!.StagingRoot);
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
                    TempPath = Path.Combine(
                        _transaction?.StagingRoot ?? CurrentInstall.Location,
                        ".chunks",
                        chunkInfo.GuidNum + ".chunk"),
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

        if (_updateTransaction != null)
        {
            try
            {
                RollbackPreparedUpdate();
            }
            catch (Exception rollbackException)
            {
                _logger.Fatal(rollbackException, "Update rollback failed; preserving recovery journal");
            }
        }

        if (_transaction != null)
        {
            try
            {
                RollbackPreparedTransaction();
            }
            catch (Exception rollbackException)
            {
                _transaction.Phase = InstallTransactionPhase.RecoveryRequired;
                PersistDurableOperationState();
                _logger.Fatal(rollbackException, "Operation rollback failed; preserving recovery journal");
            }
        }

        CurrentInstall.StatusMessage = errorMessage;
        CurrentInstall.Status = userCancellation ? ActionStatus.Cancelled : ActionStatus.Failed;
        PublishTerminal(
            CurrentInstall,
            userCancellation ? InstallTerminalOutcome.Cancelled : InstallTerminalOutcome.Failed,
            userCancellation ? null : errorMessage);
        if (userCancellation)
            _logger.Information("Installation cancelled");
        else
            _logger.Error("Installation failed: {ErrorMessage}", errorMessage);

        var state = _transaction?.Phase == InstallTransactionPhase.RecoveryRequired
            ? new InstallManagerState
            {
                CurrentInstall = CurrentInstall,
                IoQueue = [],
                CompletedChunks = [.. Operation.ResumeCompletedChunks, .. _completedChunks],
                Plan = Operation.Plan,
                Phase = InstallTransactionPhase.RecoveryRequired
            }
            : new InstallManagerState();

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
        var addedChunkGuids = new HashSet<BigInteger>();

        double totalDownloadSizeBytes = 0;
        double totalWriteSizeBytes = 0;

        foreach (var fileManifest in manifest.FileManifestList.Elements)
        {
            foreach (var chunkPart in fileManifest.ChunkParts)
            {
                if (addedChunkGuids.Add(chunkPart.GuidNum))
                {
                    var chunkInfo = manifest.CDL.GetChunkByGuidNum(chunkPart.GuidNum);
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

    public InstallItem? GameGameInQueue(string gameName)
    {
        lock (_installItemLock)
        {
            if (CurrentInstall?.AppName == gameName)
                return CurrentInstall;
            return _installQueue.FirstOrDefault(item => item.AppName == gameName);
        }
    }

    public void CancelInstall(string appName) =>
        CancelAsync(appName).GetAwaiter().GetResult();

    private async Task<InstallCommandResult> ExecuteCancelAsync(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return new InstallCommandResult(InstallCommandOutcome.Rejected, "An app name is required.");

        CancellationTokenSource? cancellation = null;
        InstallItem? cancellingItem = null;
        var paused = false;
        var recoveryRequested = false;
        lock (_operationLifecycleLock)
        {
            if (string.Equals(CurrentInstall?.AppName, appName, StringComparison.Ordinal))
            {
                _userCancellationRequested = true;
                paused = CurrentInstall.Status == ActionStatus.Paused;
                recoveryRequested = !_acceptCancellation;
                Operation.RecoveryRequested = recoveryRequested;
                CurrentInstall.Status = ActionStatus.Cancelling;
                cancellingItem = CurrentInstall;
                if (!recoveryRequested)
                    cancellation = _cancellationTokenSource;
            }
        }

        if (recoveryRequested)
        {
            if (_transaction is not null)
            {
                _transaction.Phase = InstallTransactionPhase.RecoveryRequired;
                PersistTransaction(_transaction);
            }
            InstallationStatusChanged?.Invoke(cancellingItem!);
            return new InstallCommandResult(InstallCommandOutcome.Accepted);
        }

        if (cancellation is not null)
        {
            InstallationStatusChanged?.Invoke(cancellingItem!);
            if (paused)
                await HandleInstallationStoppage("Installation cancelled", userCancellation: true);
            else
                await cancellation.CancelAsync();
            return new InstallCommandResult(InstallCommandOutcome.Accepted);
        }

        InstallItem? removedItem;
        lock (_installItemLock)
        {
            removedItem = _installQueue.FirstOrDefault(item => item.AppName == appName);
            if (removedItem is not null)
                _installQueue.Remove(removedItem);
        }
        if (removedItem is null)
            return new InstallCommandResult(InstallCommandOutcome.NotFound, "No matching operation was found.");

        removedItem.Status = ActionStatus.Cancelled;
        PublishTerminal(removedItem, InstallTerminalOutcome.Cancelled);
        _logger.Information("RemoveFromQueue: Removed {AppName} from the install queue", appName);
        return new InstallCommandResult(InstallCommandOutcome.Accepted);
    }

    public List<string> GetQueueItemNames()
    {
        lock (_installItemLock)
            return _installQueue.Select(item => item.AppName).ToList();
    }

    public List<string> GetHistoryItemsNames()
    {
        lock (_installItemLock)
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            for (var index = _installHistory.Count - 1; index >= 0; index--)
            {
                if (seen.Add(_installHistory[index].AppName))
                    result.Add(_installHistory[index].AppName);
            }
            result.Reverse();
            return result;
        }
    }

    public async Task StopProcessing()
    {
        Task completion;
        string? appName;
        lock (_operationLifecycleLock)
        {
            appName = CurrentInstall?.AppName;
            completion = _operationCompletion.Task;
        }

        if (appName is null)
            return;

        await CancelAsync(appName);
        await completion;
    }

    private void PublishTerminal(
        InstallItem item,
        InstallTerminalOutcome outcome,
        string? error = null)
    {
        var terminal = new InstallTerminalResult(
            item.AppName,
            item.Action,
            outcome,
            error,
            Operation.PlanningFailure);
        lock (_installItemLock)
            _installHistory.Add(item);
        InstallationStatusChanged?.Invoke(item);
        OperationCompleted?.Invoke(terminal);
        if (_terminalResults.TryRemove(item.AppName, out var completion))
            completion.TrySetResult(terminal);
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

    public void PauseInstall() =>
        PauseAsync().GetAwaiter().GetResult();

    private async Task<InstallCommandResult> ExecutePauseAsync()
    {
        if (!IsInstallationInProgress() || CurrentInstall is null)
            return new InstallCommandResult(InstallCommandOutcome.Rejected, "No running operation can be paused.");

        var context = Operation;
        var pausedItem = context.Item;
        _logger.Debug("Pausing installation of {Game}", pausedItem.AppName);
        context.PauseRequested = true;
        await context.Cancellation.CancelAsync();
        try
        {
            await _processingTask;
        }
        catch (OperationCanceledException)
        {
        }
        context.Stopwatch.Stop();
        if (context.Plan is { } plan && context.Transaction is { } transaction)
        {
            var verifiedPaths = new HashSet<string>(
                plan.VerifiedStageFiles.Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.PendingStageFiles)
            {
                var stagedPath = ManifestPath.ResolveUnderRoot(transaction.StagingRoot, file.Path);
                if (File.Exists(stagedPath) && string.Equals(
                        Util.CalculateSHA1(stagedPath),
                        file.Sha1,
                        StringComparison.OrdinalIgnoreCase))
                {
                    verifiedPaths.Add(file.Path);
                }
            }

            var allStageFiles = plan.VerifiedStageFiles
                .Concat(plan.PendingStageFiles)
                .ToArray();
            var verifiedFiles = allStageFiles
                .Where(file => verifiedPaths.Contains(file.Path))
                .ToImmutableArray();
            var pendingFiles = allStageFiles
                .Where(file => !verifiedPaths.Contains(file.Path))
                .ToImmutableArray();
            context.Plan = plan with
            {
                VerifiedStageFiles = verifiedFiles,
                PendingStageFiles = pendingFiles,
                RequiredStagingBytes = pendingFiles.Sum(file => file.Size)
            };
            transaction.Plan = context.Plan;
            transaction.Phase = InstallTransactionPhase.Paused;
            PersistTransaction(transaction);
        }
        else
        {
            PersistDurableOperationState();
        }

        pausedItem.Status = ActionStatus.Paused;
        InstallationStatusChanged?.Invoke(pausedItem);
        _logger.Information("Saved durable pause checkpoint for {Game}", pausedItem.AppName);
        return new InstallCommandResult(InstallCommandOutcome.Accepted);
    }

    public void ResumeInstall() =>
        ResumeAsync().GetAwaiter().GetResult();

    private InstallCommandResult ExecuteResume()
    {
        if (CurrentInstall?.Status != ActionStatus.Paused)
            return new InstallCommandResult(InstallCommandOutcome.Rejected, "No paused operation is available.");

        InstallOperationContext previous;
        lock (_operationLifecycleLock)
        {
            previous = Operation;
            var resumed = new InstallOperationContext(previous.Item, previous.OperationId)
            {
                AcceptCancellation = true,
                Plan = previous.Plan,
                Transaction = previous.Transaction
            };
            _activeContext = resumed;
        }
        previous.Dispose();

        lock (_installItemLock)
            _processingTask = ProcessNextAsync(true);
        return new InstallCommandResult(InstallCommandOutcome.Accepted);
    }

    public Task LoadPendingInstalls() => RecoverAsync();

    private async Task<InstallCommandResult> ExecuteRecoverAsync()
    {
        string jsonData;
        try
        {
            jsonData = _storage.GetInstallState();
        }
        catch (Exception)
        {
            return new InstallCommandResult(InstallCommandOutcome.NotFound, "No durable operation checkpoint exists.");
        }

        if (string.IsNullOrEmpty(jsonData))
            return new InstallCommandResult(InstallCommandOutcome.NotFound, "No durable operation checkpoint exists.");

        var state = JsonSerializer.Deserialize<InstallManagerState>(jsonData, _jsonSerializerOptions);
        if (state?.CurrentInstall is null || state.Plan is null ||
            state.Phase != InstallTransactionPhase.Paused)
        {
            return new InstallCommandResult(InstallCommandOutcome.NotFound, "No resumable operation exists.");
        }

        var planned = InstallTransactionState.Create(state.Plan, null, null);
        var read = AtomicJsonFile.ReadAndMigrate(planned.JournalPath, InstallTransactionSchema);
        if (!read.IsSuccess || read.Value is null)
            return new InstallCommandResult(InstallCommandOutcome.Rejected, "The paused operation journal is unavailable.");

        lock (_operationLifecycleLock)
        {
            _activeContext = new InstallOperationContext(
                state.CurrentInstall,
                state.Plan.OperationId)
            {
                Plan = state.Plan,
                Transaction = read.Value,
                AcceptCancellation = true
            };
        }
        CurrentInstall.Status = ActionStatus.Paused;
        InstallationStatusChanged?.Invoke(CurrentInstall);
        return new InstallCommandResult(InstallCommandOutcome.Accepted);
    }

    private async Task<InstallCommandResult> ExecuteShutdownAsync()
    {
        var current = CurrentInstall;
        if (current is null || current.Status == ActionStatus.Paused)
            return new InstallCommandResult(InstallCommandOutcome.Accepted);
        if (IsInstallationInProgress() && _acceptCancellation)
            return await ExecutePauseAsync();

        await _processingTask;
        return new InstallCommandResult(InstallCommandOutcome.Accepted);
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

internal enum InstallCommandKind
{
    Enqueue,
    Pause,
    Resume,
    Cancel,
    Shutdown,
    Recover
}

internal sealed record InstallCommandEnvelope(
    InstallCommandKind Kind,
    InstallItem? Item,
    string? AppName,
    TaskCompletionSource<InstallCommandResult> Completion);

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

internal sealed class InstallProcessTerminationException(string message) : Exception(message);

internal sealed class InstallManagerState
{
    public InstallItem? CurrentInstall { get; set; }
    public List<IoTask>? IoQueue { get; set; }
    public List<BigInteger>? CompletedChunks { get; set; }
    public InstallOperationPlan? Plan { get; set; }
    public InstallTransactionPhase? Phase { get; set; }
}
