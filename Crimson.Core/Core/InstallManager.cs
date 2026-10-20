using Crimson.Resources;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public class InstallManager : IDisposable
{
    public event Action<InstallItem> InstallationStatusChanged;
    public event Action<InstallItem> InstallProgressUpdate;
    public event Action<OperationSnapshot>? OperationUpdated;
    public event Action<OperationSnapshot>? TransferRatesUpdated;
    public event Action<OperationSnapshot>? OperationError;

    private readonly ILogger _logger;
    private readonly LibraryManager _libraryManager;
    private readonly DownloadManager _downloadManager;
    private readonly IGameShortcutManager _shortcutManager;
    private readonly IInstallPermissionChecker _permissionChecker;
    private readonly EpicInstallDiscovery? _epicInstallDiscovery;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;

    private readonly List<InstallItem> _installQueue = [];
    private readonly List<InstallItem> _installHistory = [];

    private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();
    private ConcurrentDictionary<BigInteger, List<FileManifest>> _chunkToFileManifestsDictionary = new();
    private ConcurrentDictionary<BigInteger, int> _chunkPartReferences = new();
    private readonly ConcurrentDictionary<string, byte> _ioQueueTaskSet = new();
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    private readonly object _installItemLock = new();
    private readonly int _numberOfThreads;
    private const int _progressUpdateIntervalInMS = 1000;
    private List<FileManifest> _importVerificationResult;
    private Manifest? _importVerificationManifest;

    private BlockingCollection<DownloadTask> _downloadQueue = [];
    private BlockingCollection<IoTask> _ioQueue = [];
    private BlockingCollection<BigInteger> _completedChunks = []; // Chunks that are downloaded and data written to all dependent files
    private List<Task> _downloadTasks;
    private List<Task> _installTasks;
    private CancellationTokenSource _cancellationTokenSource = new();
    private Stopwatch _installStopWatch = new();
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private Timer? _transferTimer;
    private InstallItem? _sampledInstall;
    private bool _disposed;
    private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
    private volatile bool _userCancelled;
    private bool _isFinalizing;
    private string? _stoppageReason;
    private OperationFailure? _stoppageFailure;
    private OperationFailure? _recoveryStateFailure;

    public InstallItem? CurrentInstall { get; private set; }
    public OperationSnapshot? CurrentOperation
    {
        get
        {
            lock (_installItemLock)
                return CurrentInstall is { } install ? OperationSnapshot.From(install) : null;
        }
    }

    public InstallManager(
        ILogger logger,
        LibraryManager libraryManager,
        IStoreRepository repository,
        Storage storage,
        DownloadManager downloadManager,
        IGameShortcutManager shortcutManager,
        IInstallPermissionChecker permissionChecker,
        EpicInstallDiscovery? epicInstallDiscovery = null)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _downloadManager = downloadManager;
        _shortcutManager = shortcutManager;
        _permissionChecker = permissionChecker;
        _epicInstallDiscovery = epicInstallDiscovery;
        CurrentInstall = null;
        _repository = repository;
        _storage = storage;

        _numberOfThreads = 12;
        _jsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new BigIntegerJsonConverter() }
        };
    }

    private void PublishStatusChanged(InstallItem install)
    {
        InstallationStatusChanged?.Invoke(install);
        PublishOperation(install);
    }

    private void PublishProgressUpdated(InstallItem install)
    {
        InstallProgressUpdate?.Invoke(install);
        PublishOperation(install);
    }

    private void PublishOperation(InstallItem install)
    {
        OperationSnapshot snapshot;
        lock (_installItemLock)
        {
            UpdateTransferSampling(install);
            snapshot = OperationSnapshot.From(install);
            _libraryManager.TrackOperation(snapshot);
        }
        OperationUpdated?.Invoke(snapshot);
        if (snapshot.Status != ActionStatus.Failed) return;
        lock (_installItemLock)
        {
            if (install.ErrorReported) return;
            install.ErrorReported = true;
        }
        ReportError(install, snapshot.Failure ?? OperationFailureClassifier.Classify(null));
    }

    private void UpdateTransferSampling(InstallItem install)
    {
        if (_disposed || !ReferenceEquals(CurrentInstall, install)) return;
        var active = install.Status is ActionStatus.Pending or ActionStatus.OnGoing or ActionStatus.Processing
            or ActionStatus.Paused or ActionStatus.Cancelling;
        var transferring = install.Phase == OperationPhase.Transferring &&
            install.Status is ActionStatus.OnGoing or ActionStatus.Processing;
        install.TransferRates.SetTransferring(transferring);
        if (!transferring)
        {
            install.DownloadSpeedRawMiB = 0;
            install.WriteSpeedMiB = 0;
        }
        if (!active)
        {
            StopTransferSampling();
            return;
        }
        if (ReferenceEquals(_sampledInstall, install)) return;
        if (!transferring) return;
        StopTransferSampling();
        _sampledInstall = install;
        _transferTimer = new Timer(_ => SampleTransferRates(install), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopTransferSampling()
    {
        _transferTimer?.Dispose();
        _transferTimer = null;
        _sampledInstall = null;
    }

    public void Dispose()
    {
        lock (_installItemLock)
        {
            _disposed = true;
            StopTransferSampling();
        }
    }

    private void SampleTransferRates(InstallItem install)
    {
        try
        {
            lock (_installItemLock)
            {
                if (!ReferenceEquals(_sampledInstall, install)) return;
                if (!ReferenceEquals(CurrentInstall, install))
                {
                    if (ReferenceEquals(_sampledInstall, install)) StopTransferSampling();
                    return;
                }
                var sample = install.TransferRates.Sample();
                if (sample is null) return;
                install.DownloadSpeedRawMiB = sample.DownloadMiBPerSecond;
                install.WriteSpeedMiB = sample.WriteMiBPerSecond;
                TransferRatesUpdated?.Invoke(OperationSnapshot.From(install));
            }
        }
        catch (Exception exception)
        {
            _logger.Error("Transfer-rate sampling failed ({ErrorType})", exception.GetType().Name);
        }
    }

    private void ReportError(InstallItem install, OperationFailure failure, OperationPhase? phase = null, bool showDialog = true) =>
        OperationError?.Invoke(OperationSnapshot.From(install) with
        {
            Failure = failure, Phase = phase ?? install.Phase, ShowErrorDialog = showDialog
        });

    internal void ReportSecondaryFailure(InstallItem install, OperationFailure failure)
    {
        install.Notice = failure;
        ReportError(install, failure, showDialog: false);
        PublishProgressUpdated(install);
    }

    public void ReportPreparationFailure(string appName, ActionType action, Exception exception) =>
        ReportPreparationFailure(appName, action, OperationFailureClassifier.Classify(exception));

    public void ReportPreparationFailure(string appName, ActionType action, OperationFailure failure) =>
        ReportError(new InstallItem(appName, action, string.Empty)
        {
            Status = ActionStatus.Failed,
            Phase = OperationPhase.Preparing
        }, failure, showDialog: failure.Code is "operation_state_save_failed" or "pending_state_invalid");

    private void SetPhase(InstallItem install, OperationPhase phase)
    {
        install.Phase = phase;
        PublishStatusChanged(install);
    }

    private static OperationPhase GetExecutionPhase(ActionType action) => action switch
    {
        ActionType.Uninstall => OperationPhase.Uninstalling,
        ActionType.Verify => OperationPhase.Verifying,
        ActionType.Import => OperationPhase.Importing,
        ActionType.Move => OperationPhase.Moving,
        _ => OperationPhase.Transferring
    };

    internal bool CanImportEpicInstallation(EpicInstallation source)
    {
        lock (_storage.AccountDataLock)
        {
            var game = GetOwnedEpicGame(source);
            return game is not null && game.LocalAppState?.InstallStatus is (null or InstallState.NotInstalled) &&
                GetActiveOperationForGame(source.AppName) is null;
        }
    }

    private Game? GetOwnedEpicGame(EpicInstallation source)
    {
        var owned = _libraryManager.GetOwnedGames().FirstOrDefault(game =>
            game.AppName == source.AppName && !game.IsDlc() &&
            game.AssetInfos.Windows.Namespace == source.CatalogNamespace &&
            game.AssetInfos.Windows.CatalogItemId == source.CatalogItemId);
        return owned is null ? null : _libraryManager.GetGameInfo(source.AppName);
    }

    internal async Task<bool> TryQueueEpicImportAsync(
        EpicInstallation source, int accountGeneration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = await GetEpicImportOwnerAsync();
        lock (_storage.AccountDataLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_storage.AccountGeneration != accountGeneration || owner is null || !CanImportEpicInstallation(source)) return false;
            return TryAddToQueue(new InstallItem(source.AppName, ActionType.Import, source.InstallLocation)
            {
                EpicImportSource = source,
                EpicImportOwner = owner,
                EpicImportAccountGeneration = accountGeneration
            });
        }
    }

    internal async Task<string?> GetEpicImportOwnerAsync()
    {
        var user = await _storage.GetUserData();
        return string.IsNullOrWhiteSpace(user?.AccountId) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.AccountId)));
    }

    /// <summary>
    /// Adds game to the install queue.
    /// Starts processing it immediately if no other game is in the queue
    /// </summary>
    /// <param name="item"></param>
    public void AddToQueue(InstallItem item) => TryAddToQueue(item);

    internal bool TryAddToQueue(InstallItem item)
    {
        if (item == null)
            return false;
        if (_recoveryStateFailure is { } recoveryFailure)
        {
            ReportPreparationFailure(item.AppName, item.Action, recoveryFailure);
            return false;
        }

        // Reject both queued duplicates and the currently processing game.
        if (GameGameInQueue(item.AppName) is not null)
        {
            _logger.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
            return false;
        }

        var gameData = ValidateQueuedGame(item);
        if (gameData is null) return false;

        NormalizeQueuedAction(item, gameData);

        _logger.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        item.Status = ActionStatus.Pending;
        item.Phase = OperationPhase.Queued;
        if (!_libraryManager.TryQueueOperation(OperationSnapshot.From(item), () => _installQueue.Add(item),
            excludeRelatedOperations: item.EpicImportSource is not null)) return false;
        PublishStatusChanged(item);
        if (CurrentInstall == null)
            ProcessNext();
        return true;
    }

    private void NormalizeQueuedAction(InstallItem item, Game game)
    {
        if (item.Action is ActionType.Repair or ActionType.Uninstall || game.LocalAppState?.InstallStatus != InstallState.Broken)
            return;
        _logger.Warning("AddToQueue: {AppName} is broken, forcing repair", item.AppName);
        item.Action = ActionType.Repair;
    }

    private Game? ValidateQueuedGame(InstallItem item)
    {
        var game = _libraryManager.GetGameInfo(item.AppName);
        if (game is null)
        {
            ReportPreparationFailure(item.AppName, item.Action, new OperationFailure(
                OperationFailureKind.Unknown, "library_item_unavailable", UiStrings.ThisGameIsNoLongerAvailableInTheCurrentLibrary,
                UiStrings.RefreshTheLibraryAndTryAgain, false));
            return null;
        }
        if (item.Action is not (ActionType.Install or ActionType.Import) &&
            game.LocalAppState?.InstallStatus is null or InstallState.NotInstalled)
        {
            ReportPreparationFailure(item.AppName, item.Action, new OperationFailure(
                OperationFailureKind.Unknown, "game_not_installed", UiStrings.ThisOperationRequiresAnInstalledGame,
                UiStrings.InstallOrImportTheGameFirst, false));
            return null;
        }
        return game;
    }

    private async void ProcessNext(bool isResuming = false)
    {
        try
        {
            if (isResuming == false && (CurrentInstall != null || _installQueue.Count <= 0)) return;

            if (!await BeginNextOperationAsync(isResuming)) return;

            if (CurrentInstall.Action is ActionType.Import or ActionType.Move)
            {
                await UpdateInstalledGameStatus();
                if (CurrentInstall != null && _cancellationTokenSource.IsCancellationRequested)
                    await HandleInstallationStoppage(GetStoppageReason());
                return;
            }

            _installStopWatch.Reset();
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

            if (_cancellationTokenSource.IsCancellationRequested)
            {
                await HandleInstallationStoppage(GetStoppageReason());
                return;
            }

            await UpdateInstalledGameStatus();

        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ProcessNext failed");
            await HandleInstallationStoppage("An error occurred during installation", ex);
        }
    }

    private async Task<bool> BeginNextOperationAsync(bool isResuming)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _userCancelled = false;
        _stoppageReason = null;
        _stoppageFailure = null;
        _importVerificationResult = null;
        _importVerificationManifest = null;
        lock (_installItemLock)
            _isFinalizing = false;
        if (!isResuming) await PrepareTasks();
        if (CurrentInstall is null) return false;
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            await HandleInstallationStoppage(GetStoppageReason());
            return false;
        }
        CurrentInstall.Status = ActionStatus.Processing;
        CurrentInstall.Phase = GetExecutionPhase(CurrentInstall.Action);
        PublishStatusChanged(CurrentInstall);
        return true;
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

            var currentInstall = CurrentInstall;
            if (currentInstall == null) return;
            currentInstall.Phase = OperationPhase.Preparing;
            PublishStatusChanged(currentInstall);
            _logger.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                currentInstall.Action, currentInstall.AppName, currentInstall.Location);

            var gameData = _libraryManager.GetGameInfo(currentInstall.AppName);
            var manifestData = await GetOperationManifestBytes(currentInstall, gameData);

            _logger.Information("ProcessNext: Parsing game manifest");
            var data = currentInstall.Action == ActionType.Import
                ? await Task.Run(() => Manifest.ReadAll(manifestData), _cancellationTokenSource.Token)
                : Manifest.ReadAll(manifestData);
            ApplyOperationInstallTags(currentInstall, data);

            // TODO Handle stats if game is installed


            if (currentInstall.Action == ActionType.Install)
            {
                // create CurrentInstall.folder if it doesn't exist
                if (!Directory.Exists(currentInstall.Location))
                {
                    Directory.CreateDirectory(currentInstall.Location);
                    _logger.Debug("Folder created at: {location}", currentInstall.Location);
                }
            }

            var permission = _permissionChecker.Check(currentInstall.Location);
            if (!permission.CanWrite)
            {
                _logger.Warning(
                    "Install location write check failed with {ErrorType}; cleanup result {CleanupErrorType}",
                    permission.ErrorType,
                    permission.CleanupErrorType);
                throw new UnauthorizedAccessException("No write permissions to install location");
            }

            ResetQueues();
            await PrepareTasksForAction(currentInstall, gameData, data, downloadedChunks);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PrepareTasks: Exception occured while preparing tasks");
            throw;
        }
    }

    private Task<byte[]> GetOperationManifestBytes(InstallItem install, Game game)
    {
        if (install.Action == ActionType.Import && install.EpicImportSource is not null)
            return GetEpicImportManifestBytesAsync(install, _cancellationTokenSource.Token);
        if (install.Action is (ActionType.Uninstall or ActionType.Move) &&
            (game.IsDlc() || !string.IsNullOrWhiteSpace(game.LocalAppState?.Version)))
            return GetInstalledManifestBytes(install.AppName);
        return GetManifestDataWithCaching(install.AppName, _cancellationTokenSource.Token);
    }

    private async Task<byte[]> GetEpicImportManifestBytesAsync(InstallItem install, CancellationToken cancellationToken)
    {
        var source = install.EpicImportSource!;
        var generation = install.EpicImportAccountGeneration ??
            throw new InvalidDataException("The import account could not be verified. Start a new import.");
        RequireEpicImportOwnership(install, generation);
        if (install.EpicImportOwner is null || install.EpicImportOwner != await GetEpicImportOwnerAsync())
            throw new InvalidDataException("This import belongs to a different account. Start a new import.");
        RequireEpicImportOwnership(install, generation);
        var discovery = _epicInstallDiscovery ?? throw new InvalidDataException("Epic installation detection is unavailable.");
        var bytes = await discovery.ReadManifestAsync(source, cancellationToken);
        var manifest = await Task.Run(() => Manifest.ReadAll(bytes), cancellationToken);
        if (manifest.ManifestMeta.AppName != source.AppName || manifest.ManifestMeta.BuildVersion != source.BuildVersion)
            throw new InvalidDataException("The installed Epic manifest does not match this game and build.");
        RequireEpicImportOwnership(install, generation);
        await _storage.CacheManifestBytes(source.AppName, source.BuildVersion, bytes);
        var cached = await _storage.GetCachedManifestBytes(source.AppName, source.BuildVersion);
        if (cached is null || !bytes.AsSpan().SequenceEqual(cached))
            throw new IOException("The installed Epic manifest could not be saved in Crimson.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireEpicImportOwnership(install, generation);
        return bytes;
    }

    private void RequireEpicImportOwnership(InstallItem install, int generation)
    {
        lock (_storage.AccountDataLock)
        {
            var source = install.EpicImportSource!;
            if (_storage.AccountGeneration != generation || GetOwnedEpicGame(source) is null ||
                install.AppName != source.AppName || install.Location != source.InstallLocation)
                throw new InvalidDataException("The game is no longer available for import in this account.");
        }
    }

    private async Task PrepareTasksForAction(
        InstallItem install,
        Game gameData,
        Manifest data,
        List<BigInteger> downloadedChunks)
    {
        switch (install.Action)
        {
            case ActionType.Install:
                await _downloadManager.InitializeMirrors(gameData.BaseUrls);
                GetChunksToDownload(data, downloadedChunks);
                break;
            case ActionType.Update:
                await PrepareUpdateTasks(gameData, data);
                break;
            case ActionType.Repair:
                SetPhase(install, OperationPhase.Verifying);
                await PrepareRepairTasks(gameData, data);
                break;
            case ActionType.Uninstall:
                if (gameData.IsDlc())
                    await PrepareDlcUninstallTasks(install, data);
                else
                    await PrepareBaseUninstallTasks(install, gameData, data);
                break;
            case ActionType.Import:
                SetPhase(install, OperationPhase.Importing);
                await PrepareImportTasks(install, data);
                break;
            case ActionType.Move:
                SetPhase(install, OperationPhase.Moving);
                PrepareMoveTasks(install);
                break;
        }
    }

    private void PrepareUninstallTasks(InstallItem install, Manifest data) =>
        QueueUninstallFiles(install, data.FileManifestList.Elements);

    private async Task PrepareBaseUninstallTasks(InstallItem install, Game game, Manifest data)
    {
        var files = data.FileManifestList.Elements.ToList();
        foreach (var dlc in GetInstalledDlcsInRoot(game, install.Location))
        {
            var manifest = Manifest.ReadAll(await GetInstalledManifestBytes(dlc.AppName));
            foreach (var file in manifest.FileManifestList.Elements)
            {
                var path = InstallPathPolicy.ResolveFile(dlc.LocalAppState!.InstallPath, file.Filename);
                files.Add(new FileManifest
                {
                    Filename = Path.GetRelativePath(install.Location, path),
                    FileSize = file.FileSize
                });
            }
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var uniqueFiles = files.GroupBy(file => InstallPathPolicy.ResolveFile(install.Location, file.Filename), comparer)
            .Select(group => group.First()).ToList();
        QueueUninstallFiles(install, uniqueFiles);
    }

    private List<Game> GetInstalledDlcsInRoot(Game game, string root) =>
        _libraryManager.GetDlcsForGame(game.AppName)
            .Where(dlc => dlc.LocalAppState?.InstallStatus is not null and not InstallState.NotInstalled)
            .Where(dlc => IsInstallWithinRoot(dlc.LocalAppState!.InstallPath, root))
            .ToList();

    private static bool IsInstallWithinRoot(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private async Task<byte[]> GetInstalledManifestBytes(string appName)
    {
        if (!_storage.LocalAppStateDictionary.TryGetValue(appName, out var state) ||
            string.IsNullOrWhiteSpace(state.Version))
            throw new InvalidDataException("The installed manifest is unavailable. Removal was not started.");

        var bytes = await _storage.GetCachedManifestBytes(appName, state.Version);
        return bytes ?? throw new InvalidDataException("The installed manifest is unavailable. Removal was not started.");
    }

    private async Task PrepareDlcUninstallTasks(InstallItem install, Manifest data)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var protectedPaths = new HashSet<string>(comparer);
        foreach (var state in _storage.LocalAppStateDictionary.Values.ToList())
        {
            if (state.AppName == install.AppName || state.InstallStatus == InstallState.NotInstalled ||
                string.IsNullOrWhiteSpace(state.InstallPath))
                continue;
            if (!InstallRootsOverlap(install.Location, state.InstallPath)) continue;

            var bytes = await GetInstalledManifestBytes(state.AppName);
            var manifest = Manifest.ReadAll(bytes);
            foreach (var file in manifest.FileManifestList.Elements)
                protectedPaths.Add(InstallPathPolicy.ResolveFile(state.InstallPath, file.Filename));
        }

        var removable = data.FileManifestList.Elements.Where(file =>
            !protectedPaths.Contains(InstallPathPolicy.ResolveFile(install.Location, file.Filename))).ToList();
        QueueUninstallFiles(install, removable);
    }

    private static bool InstallRootsOverlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var firstRoot = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var secondRoot = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return firstRoot.StartsWith(secondRoot, comparison) || secondRoot.StartsWith(firstRoot, comparison);
    }

    private void QueueUninstallFiles(InstallItem install, IEnumerable<FileManifest> files)
    {
        foreach (var file in files)
        {
            var path = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
            install.TotalWriteSizeMb += file.FileSize / 1024.0 / 1024.0;
            _ioQueue.Add(new IoTask
            {
                DestinationFilePath = path,
                TaskType = IoTaskType.Delete,
                Size = file.FileSize
            });
        }
    }

    private void ApplyOperationInstallTags(InstallItem install, Manifest data)
    {
        var tags = install.Action == ActionType.Import ? install.EpicImportSource?.InstallTags
            : _libraryManager.GetGameInfo(install.AppName)?.LocalAppState?.InstallTags;
        ApplyInstallTags(data, tags);
    }

    private static void ApplyInstallTags(Manifest data, string[]? tags)
    {
        if (tags is null) return;
        var selected = tags.ToHashSet(StringComparer.Ordinal);
        data.FileManifestList.Elements.RemoveAll(file =>
            file.InstallTags.Count > 0 && !file.InstallTags.Any(selected.Contains));
        data.FileManifestList.Count = data.FileManifestList.Elements.Count;
    }

    private async Task PrepareImportTasks(InstallItem install, Manifest data)
    {
        var cancellationToken = _cancellationTokenSource.Token;
        var result = await Task.Run(() => VerifyImportFilesAsync(install, data, cancellationToken), cancellationToken);
        if (result.FoundFiles == 0)
        {
            install.Failure = new OperationFailure(
                OperationFailureKind.Verification,
                "import_location_invalid",
                UiStrings.NoGameFilesWereFoundInTheSelectedFolder,
                UiStrings.ChooseTheFolderContainingTheGameFilesThenImportAgain,
                false);
            throw new InvalidDataException("No game files were found in the selected folder.");
        }
        _importVerificationResult = result.InvalidFiles;
        _importVerificationManifest = data;
        _logger.Information("Import verification: {Invalid}/{Total} files require repair",
            result.InvalidFiles.Count, data.FileManifestList.Elements.Count);
    }

    private static async Task<(List<FileManifest> InvalidFiles, int FoundFiles)> VerifyImportFilesAsync(
        InstallItem install, Manifest data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(install.Location))
            throw new DirectoryNotFoundException("Import folder does not exist");

        var invalidFiles = new List<FileManifest>();
        var foundFiles = 0;
        foreach (var file in data.FileManifestList.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
            if (!File.Exists(path))
            {
                invalidFiles.Add(file);
                continue;
            }
            foundFiles++;
            if (!await MatchesImportFileAsync(path, file, cancellationToken)) invalidFiles.Add(file);
        }
        return (invalidFiles, foundFiles);
    }

    private static async Task<bool> MatchesImportFileAsync(string path, FileManifest file, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length != file.FileSize) return false;
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return hash.AsSpan().SequenceEqual(file.ShaHash);
    }

    private void PrepareMoveTasks(InstallItem install)
    {
        if (!_storage.AreOnSameVolume(install.Location, install.MoveLocation))
            throw new IOException("Cross-volume moves are not supported. Please uninstall and reinstall to the new location.");

        if (Directory.Exists(install.MoveLocation))
            throw new IOException("Destination directory already exists");

        _logger.Information("Move: Moving {AppName} from {Src} to {Dest}",
            install.AppName, install.Location, install.MoveLocation);
        Directory.Move(install.Location, install.MoveLocation);
        _logger.Information("Move: Successfully moved {AppName}", install.AppName);
    }

    private void ResetQueues()
    {
        _downloadQueue.Dispose();
        _ioQueue.Dispose();
        _downloadQueue = [];
        _ioQueue = [];
        _ioQueueTaskSet.Clear();
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
            var install = CurrentInstall ?? throw new InvalidOperationException("No active installation for downloads.");
            foreach (var downloadTask in _downloadQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                try
                {

                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    var downloaded = await _downloadManager.DownloadFileWithFallback(
                        downloadTask.Url,
                        downloadTask.TempPath,
                        cancellationToken: _cancellationTokenSource.Token,
                        expectedSize: downloadTask.ChunkInfo.FileSize,
                        retryStateChanged: retrying => UpdateDownloadRetryState(install, retrying),
                        bytesTransferred: install.TransferRates.RecordDownload);
                    if (!downloaded)
                        throw new HttpRequestException("The required download could not be completed.");

                    UpdateDownloadProgress(downloadTask.ChunkInfo.FileSize);
                    CreateIoTasksForChunk(downloadTask);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "ProcessDownloadQueue failed");
                    RequestStoppage("Download task failed", ex);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RequestStoppage("Download worker failed", exception);
        }
    }

    private void UpdateDownloadRetryState(InstallItem install, bool retrying)
    {
        lock (_installItemLock)
        {
            if (!ReferenceEquals(CurrentInstall, install)) return;
            var wasRetrying = install.IsRetrying;
            install.RetryingDownloadCount = Math.Max(0, install.RetryingDownloadCount + (retrying ? 1 : -1));
            if (wasRetrying != install.IsRetrying)
                PublishProgressUpdated(install);
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
                if (!_ioQueueTaskSet.TryAdd(ioTaskHashString, 0))
                    continue;

                var task = new IoTask()
                {
                    SourceFilePath = downloadTask.TempPath,
                    DestinationFilePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, fileManifest.Filename),
                    TaskType = IoTaskType.Copy,
                    Size = part.Size,
                    Offset = part.Offset,
                    FileOffset = part.FileOffset,
                    DestinationFileSize = fileManifest.FileSize,
                    GuidNum = part.GuidNum,
                    SourceChunkGuidNum = downloadTask.GuidNum
                };
                _ioQueue.Add(task);
            }
        }
    }

    private async Task ProcessIOQueue()
    {
        try
        {
            var install = CurrentInstall ?? throw new InvalidOperationException("No active installation for IO processing.");
            foreach (var ioTask in _ioQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
            {
                try
                {
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    switch (ioTask.TaskType)
                    {
                        case IoTaskType.Copy:
                            await ProcessCopyTask(ioTask, install.Location);
                            break;
                        case IoTaskType.Delete:
                            File.Delete(InstallPathPolicy.RequireFile(install.Location, ioTask.DestinationFilePath));
                            break;
                    }
                    UpdateInstallWriteProgress(ioTask.Size);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "ProcessIoQueue: IO task failed");
                    RequestStoppage("Io Task failed", ex);
                    return;
                }

            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RequestStoppage("File worker failed", exception);
        }
    }

    private async Task ProcessCopyTask(IoTask ioTask, string installRoot)
    {
        var transferRates = CurrentInstall?.TransferRates;
        InstallPathPolicy.RequireFile(installRoot, ioTask.DestinationFilePath);
        EnsureDirectoryExists(ioTask.DestinationFilePath);

        // Ensure there is a lock object for each destination file
        var fileLock =
            _fileLocksConcurrentDictionary.GetOrAdd(ioTask.DestinationFilePath, new object());

        var compressedChunkData = await File.ReadAllBytesAsync(ioTask.SourceFilePath, _cancellationTokenSource.Token);
        var chunk = Chunk.ReadBuffer(compressedChunkData);
        RequireChunkPartRange(ioTask, chunk.Data.LongLength);
        lock (fileLock)
        {
            var destination = InstallPathPolicy.RequireFile(installRoot, ioTask.DestinationFilePath);
            using var fileStream = new FileStream(destination, FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None);
            if (ioTask.DestinationFileSize > 0 && fileStream.Length != ioTask.DestinationFileSize)
                fileStream.SetLength(ioTask.DestinationFileSize);

            fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);

            // Since chunk offset is a long we cannot use it directly in File stream write or read
            // Use a memory stream to seek to the chunk offset
            using var memoryStream = new MemoryStream(chunk.Data);
            memoryStream.Seek(ioTask.Offset, SeekOrigin.Begin);

            var remainingBytesToWrite = ioTask.Size;
            // Buffer size is irrelevant as write is continuous
            const int bufferSize = 4096;
            var buffer = new byte[bufferSize];

            while (remainingBytesToWrite > 0)
            {
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                var bytesToRead = (int)Math.Min(bufferSize, remainingBytesToWrite);
                var bytesRead = memoryStream.Read(buffer, 0, bytesToRead);
                if (bytesRead == 0) throw new EndOfStreamException("Chunk data ended before the requested part was copied.");
                fileStream.Write(buffer, 0, bytesRead);
                transferRates?.RecordWrite(bytesRead);

                remainingBytesToWrite -= bytesRead;
            }

            fileStream.Flush();
        }

        // Check for references to the chunk and decrement by one
        int newCount = _chunkPartReferences.AddOrUpdate(
            ioTask.GuidNum,
            (key) => 0, // Not expected to be called as the key should exist
            (key, oldValue) =>
            {
                return oldValue - 1;
            }
        );

        // Check if the updated count is 0 or less
        if (newCount <= 0 && _chunkPartReferences.TryRemove(ioTask.GuidNum, out _))
        {
            _completedChunks.Add(ioTask.SourceChunkGuidNum);
            // Delete the file if successfully removed
            File.Delete(ioTask.SourceFilePath);
        }
    }

    private static void RequireChunkPartRange(IoTask task, long decodedLength)
    {
        if (task.Offset < 0 || task.Size < 0 || task.Offset > decodedLength || task.Size > decodedLength - task.Offset)
            throw new InvalidDataException("The requested chunk part exceeds the decoded chunk data.");
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
        var install = CurrentInstall;
        if (install is null)
        {
            _logger.Error("UpdateInstalledGameStatus: Current install is null");
            return;
        }

        try
        {
            if (!IsInstallationInProgress())
                return;

            await StopInstallWorkersAsync(install);
            if (!TryBeginFinalization(install))
            {
                await HandleInstallationStoppage("Cancel install");
                return;
            }
            var game = GetInstalledGame(install);
            var localAppState = GetLocalAppState(install);
            if (install.Action is ActionType.Install or ActionType.Update or ActionType.Repair or ActionType.Verify)
                SetPhase(install, OperationPhase.Verifying);
            await ApplyCompletedActionAsync(install, game, localAppState);
            SetPhase(install, OperationPhase.Finalizing);
            await CreateRequestedShortcutsAsync(install, game, localAppState);
            CompleteInstallation(install);
        }
        catch (OperationCanceledException) when (_userCancelled)
        {
            await HandleInstallationStoppage("Cancel install");
        }
        catch (Exception ex)
        {
            FailCurrentInstallation(ex);
        }
    }

    private bool TryBeginFinalization(InstallItem install)
    {
        lock (_installItemLock)
        {
            if (!ReferenceEquals(CurrentInstall, install) || _userCancelled || _cancellationTokenSource.IsCancellationRequested)
                return false;

            _isFinalizing = true;
            return true;
        }
    }

    private async Task StopInstallWorkersAsync(InstallItem install)
    {
        if (install.Action is not ActionType.Import and not ActionType.Move)
        {
            await Task.Delay(2000, _cancellationTokenSource.Token);
        }

        _installStopWatch.Reset();
    }

    private Game GetInstalledGame(InstallItem install)
    {
        var game = _libraryManager.GetGameInfo(install.AppName);
        if (game is not null)
            return game;

        _logger.Error(
            "UpdateInstalledGameStatus: Found no game data for app name: {AppName}",
            install.AppName);
        throw new Exception("Invalid game data");
    }

    private LocalAppState GetLocalAppState(InstallItem install)
    {
        if (_storage.LocalAppStateDictionary.TryGetValue(install.AppName, out var localAppState))
            return localAppState;
        if (install.Action == ActionType.Import)
            return new LocalAppState { AppName = install.AppName };

        _logger.Error(
            "UpdateInstalledGameStatus: Found no installed game data for {AppName}",
            install.AppName);
        throw new Exception("Invalid installed game data");
    }

    private async Task ApplyCompletedActionAsync(
        InstallItem install,
        Game game,
        LocalAppState localAppState)
    {
        switch (install.Action)
        {
            case ActionType.Uninstall:
                CompleteUninstall(install, game, localAppState);
                break;
            case ActionType.Move:
                CompleteMove(install, game, localAppState);
                break;
            case ActionType.Import:
                await CompleteImportAsync(install, game, localAppState);
                break;
            default:
                await CompleteInstallAsync(install, game, localAppState);
                break;
        }
    }

    private void CompleteUninstall(InstallItem install, Game game, LocalAppState localAppState)
    {
        var dlcs = GetInstalledDlcsInRoot(game, install.Location);
        ClearInstallationState(localAppState);
        SaveLocalAppState(game, localAppState);
        foreach (var dlc in dlcs)
        {
            ClearInstallationState(dlc.LocalAppState!);
            SaveLocalAppState(dlc, dlc.LocalAppState!);
        }
        TryRemoveShortcuts(game);
        _logger.Information(
            "UpdateInstalledGameStatus: Uninstall complete for {AppName}",
            install.AppName);
    }

    private static void ClearInstallationState(LocalAppState state)
    {
        state.InstallStatus = InstallState.NotInstalled;
        state.InstallPath = null;
        state.Version = null;
        state.InstallSize = 0;
        state.InstallTags = null;
        state.LastUpdatedAt = null;
        state.Executable = null;
    }

    private void CompleteMove(InstallItem install, Game game, LocalAppState localAppState)
    {
        var dlcs = GetInstalledDlcsInRoot(game, install.Location);
        localAppState.InstallPath = install.MoveLocation;
        SaveLocalAppState(game, localAppState);
        foreach (var dlc in dlcs)
        {
            var relative = Path.GetRelativePath(install.Location, dlc.LocalAppState!.InstallPath);
            dlc.LocalAppState.InstallPath = Path.GetFullPath(Path.Combine(install.MoveLocation, relative));
            SaveLocalAppState(dlc, dlc.LocalAppState);
        }
        _logger.Information(
            "UpdateInstalledGameStatus: Move complete for {AppName}",
            install.AppName);
    }

    private Task CompleteImportAsync(InstallItem install, Game game, LocalAppState localAppState)
    {
        lock (_storage.AccountDataLock)
        {
            RequireEpicImportSession(install);
            return CompleteVerifiedImport(install, game, localAppState);
        }
    }

    private void RequireEpicImportSession(InstallItem install)
    {
        if (install.EpicImportSource is null) return;
        var generation = install.EpicImportAccountGeneration ??
            throw new InvalidDataException("The import account could not be verified. Start a new import.");
        RequireEpicImportOwnership(install, generation);
    }

    private Task CompleteVerifiedImport(
        InstallItem install,
        Game game,
        LocalAppState localAppState)
    {
        var manifest = _importVerificationManifest ?? throw new InvalidOperationException("The verified import manifest is unavailable.");
        var invalidFiles = _importVerificationResult ?? throw new InvalidOperationException("Import verification was not completed.");
        localAppState.InstallStatus = invalidFiles.Count > 0 ? InstallState.Broken : InstallState.Installed;
        localAppState.InstallTags = install.EpicImportSource?.InstallTags?.ToArray();
        ApplyManifestState(install, game, localAppState, manifest);
        if (invalidFiles.Count > 0) localAppState.Version = null;
        else if (manifest.ManifestMeta.BuildVersion != game.AssetInfos.Windows.BuildVersion)
            localAppState.InstallStatus = InstallState.NeedUpdate;
        SaveLocalAppState(game, localAppState);

        var totalFiles = manifest.FileManifestList.Elements.Count;
        install.StatusMessage = invalidFiles.Count == 0
            ? UiStrings.Format(UiStrings.ImportVerifiedFilesFormat, totalFiles)
            : UiStrings.Format(UiStrings.ImportRepairFilesFormat, totalFiles, invalidFiles.Count);
        _importVerificationResult = null;
        _importVerificationManifest = null;
        _logger.Information(
            "UpdateInstalledGameStatus: Import complete for {AppName}, status: {Status}",
            install.AppName,
            localAppState.InstallStatus);
        return Task.CompletedTask;
    }

    private async Task CompleteInstallAsync(
        InstallItem install,
        Game game,
        LocalAppState localAppState)
    {
        var manifest = await GetManifestAsync(install, game);
        ApplyOperationInstallTags(install, manifest);
        var invalidFiles = await VerifyFiles(install.Location, manifest.FileManifestList.Elements);
        SetVerificationStatus(install, localAppState, invalidFiles.Count);
        ApplyManifestState(install, game, localAppState, manifest);
        if (invalidFiles.Count == 0 && install.Action is ActionType.Install or ActionType.Update)
            localAppState.LastUpdatedAt = DateTimeOffset.UtcNow;
        SaveLocalAppState(game, localAppState);
        EnsureVerificationSucceeded(install, invalidFiles.Count);
    }

    private async Task<Manifest> GetManifestAsync(InstallItem install, Game game)
    {
        var buildVersion = game.AssetInfos.Windows.BuildVersion;
        var manifestBytes = await _storage.GetCachedManifestBytes(install.AppName, buildVersion);
        if (manifestBytes is { Length: > 0 })
            return Manifest.ReadAll(manifestBytes);

        var urlData = await _repository.GetManifestUrls(
            game.AssetInfos.Windows.Namespace,
            game.AssetInfos.Windows.CatalogItemId,
            game.AppName);
        if (urlData is null)
        {
            _logger.Error(
                "UpdateInstalledGameStatus: Failed to get manifest urls for {AppName}",
                install.AppName);
            throw new Exception("Cannot fetch manifest data");
        }

        manifestBytes = await _repository.GetGameManifest(urlData);
        await _storage.CacheManifestBytes(install.AppName, buildVersion, manifestBytes);
        return Manifest.ReadAll(manifestBytes);
    }

    private void SetVerificationStatus(
        InstallItem install,
        LocalAppState localAppState,
        int invalidFileCount)
    {
        if (invalidFileCount > 0)
        {
            _logger.Warning(
                "UpdateInstalledGameStatus: {Count} files failed verification for {AppName}. Marking as Broken.",
                invalidFileCount,
                install.AppName);
            localAppState.InstallStatus = InstallState.Broken;
            return;
        }

        _logger.Information(
            "UpdateInstalledGameStatus: Verification successful for {AppName}",
            install.AppName);
        localAppState.InstallStatus = InstallState.Installed;
    }

    private static void EnsureVerificationSucceeded(InstallItem install, int invalidFileCount)
    {
        if (invalidFileCount == 0)
            return;

        install.StatusMessage = $"{invalidFileCount} files failed verification";
        install.Failure = OperationFailure.Verification(invalidFileCount);
        throw new InvalidDataException(install.StatusMessage);
    }

    private static void ApplyManifestState(
        InstallItem install,
        Game game,
        LocalAppState localAppState,
        Manifest manifest)
    {
        localAppState.BaseUrls = game.BaseUrls;
        localAppState.CanRunOffline = game.Metadata?.CustomAttributes?.CanRunOffline?.Value == "true";
        localAppState.Executable = manifest.ManifestMeta.LaunchExe;
        localAppState.InstallPath = install.Location;
        localAppState.LaunchParameters = manifest.ManifestMeta.LaunchCommand;
        localAppState.RequiresOt = game.Metadata?.CustomAttributes?.OwnershipToken?.Value == "true";
        localAppState.Version = manifest.ManifestMeta.BuildVersion;
        localAppState.InstallSize = manifest.FileManifestList.Elements.Sum(file => file.FileSize);
        localAppState.Title = game.AppTitle;

        if (manifest.ManifestMeta.UninstallActionPath is not null)
        {
            localAppState.Uninstaller = new Dictionary<string, string>
            {
                { manifest.ManifestMeta.UninstallActionPath, manifest.ManifestMeta.UninstallActionArgs }
            };
        }
    }

    private void SaveLocalAppState(Game game, LocalAppState localAppState)
    {
        game.LocalAppState = localAppState;
        _storage.AddToLocalAppState(game.AppName, localAppState);
        _libraryManager.UpdateGameInfo(game);
    }

    private void CompleteInstallation(InstallItem install)
    {
        install.Status = ActionStatus.Success;
        _installHistory.Add(install);
        PublishStatusChanged(install);
        CurrentInstall = null;
        ProcessNext();
    }

    private void FailCurrentInstallation(Exception exception)
    {
        _logger.Error(exception, "UpdateInstalledGameStatus failed");
        if (CurrentInstall is not null)
        {
            CurrentInstall.Status = ActionStatus.Failed;
            CurrentInstall.Failure ??= OperationFailureClassifier.Classify(exception);
            _installHistory.Add(CurrentInstall);
            PublishStatusChanged(CurrentInstall);
        }

        CurrentInstall = null;
        ProcessNext();
    }

    private void TryRemoveShortcuts(Game game)
    {
        try
        {
            _shortcutManager.Remove(game);
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to remove shortcuts: {ErrorType}", ex.GetType().Name);
            if (CurrentInstall is { } install)
                ReportSecondaryFailure(install, new OperationFailure(OperationFailureKind.Storage,
                    "shortcut_cleanup_failed", UiStrings.TheGameWasRemovedButItsShortcutsCouldNotBeRemoved,
                    UiStrings.RemoveTheRemainingCrimsonShortcutsManually, false));
        }
    }

    private async Task CreateRequestedShortcutsAsync(
        InstallItem install,
        Game game,
        LocalAppState localAppState)
    {
        if (install.Action != ActionType.Install || localAppState.InstallStatus != InstallState.Installed || game.IsDlc())
            return;

        _cancellationTokenSource.Token.ThrowIfCancellationRequested();
        if (install.CreateStartMenuShortcut)
            await TryCreateShortcutAsync(game, GameShortcutLocation.StartMenu);
        _cancellationTokenSource.Token.ThrowIfCancellationRequested();
        if (install.CreateDesktopShortcut)
            await TryCreateShortcutAsync(game, GameShortcutLocation.Desktop);
    }

    private async Task TryCreateShortcutAsync(Game game, GameShortcutLocation location)
    {
        var cancellationToken = _cancellationTokenSource.Token;
        try
        {
            await _shortcutManager.CreateAsync(game, location, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to create shortcut: {ErrorType}", ex.GetType().Name);
            if (CurrentInstall is { } install)
                ReportSecondaryFailure(install, new OperationFailure(OperationFailureKind.Storage,
                    location == GameShortcutLocation.Desktop ? "desktop_shortcut_failed" : "start_menu_shortcut_failed",
                    UiStrings.TheGameWasInstalledButARequestedShortcutCouldNotBeCreated,
                    UiStrings.CreateTheShortcutAgainFromGameProperties, false));
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
        var invalidFilesBag = new ConcurrentBag<FileManifest>();
        await Parallel.ForEachAsync(fileManifestLists, options, async (manifest, token) =>
            {
                try
                {
                    var filePath = InstallPathPolicy.ResolveFile(installPath, manifest.Filename);

                    // Check if file exists and add to list if it doesn't
                    if (!File.Exists(filePath))
                    {
                        _logger.Debug("VerifyFiles: Missing {Filename}", manifest.Filename);
                        invalidFilesBag.Add(manifest);
                        return;
                    }

                    var fileSha1 = FileHash.ComputeSha1(filePath);
                    var expectedHash = BitConverter.ToString(manifest.ShaHash).Replace("-", "").ToLowerInvariant();
                    if (fileSha1 != expectedHash)
                    {
                        var fileInfo = new FileInfo(filePath);
                        _logger.Debug("VerifyFiles: Hash mismatch {Filename} (size={Size}, expected={Expected}, actual={Actual})",
                            manifest.Filename, fileInfo.Length, expectedHash, fileSha1);
                        invalidFilesBag.Add(manifest);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "VerifyFiles: Error verifying file {Filename}", manifest.Filename);
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
                var filePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, fileManifest.Filename);
                EnsureDirectoryExists(filePath);
                File.Create(filePath).Dispose();
            }
        }
    }

    private async Task<Manifest?> GetUpdateBaseManifestAsync()
    {
        var localAppState = _storage.LocalAppStateDictionary
            .FirstOrDefault(g => g.Key == CurrentInstall.AppName).Value;
        if (localAppState == null || string.IsNullOrEmpty(localAppState.Version))
        {
            _logger.Warning("PrepareUpdateTasks: No installed version info, falling back to full install");
            return null;
        }
        var bytes = await _storage.GetCachedManifestBytes(CurrentInstall.AppName, localAppState.Version);
        if (bytes == null || bytes.Length < 1)
        {
            _logger.Warning("PrepareUpdateTasks: Old manifest not cached, falling back to full install");
            return null;
        }
        var manifest = Manifest.ReadAll(bytes);
        ApplyInstallTags(manifest, localAppState.InstallTags);
        return manifest;
    }

    /// <summary>
    /// Prepare update tasks by comparing old and new manifests.
    /// Downloads only chunks for changed/added files and deletes removed files.
    /// Falls back to full reinstall if old manifest is unavailable.
    /// </summary>
    private async Task PrepareUpdateTasks(Game gameData, Manifest newManifest)
    {
        var oldManifest = await GetUpdateBaseManifestAsync();
        if (oldManifest is null)
        {
            CurrentInstall.Action = ActionType.Install;
            await _downloadManager.InitializeMirrors(gameData.BaseUrls);
            GetChunksToDownload(newManifest);
            return;
        }
        _logger.Information("PrepareUpdateTasks: Comparing manifests for {AppName}", CurrentInstall.AppName);

        // Build lookup of old file hashes by filename
        var oldFileHashes = new Dictionary<string, byte[]>();
        foreach (var oldFile in oldManifest.FileManifestList.Elements)
        {
            oldFileHashes[oldFile.Filename] = oldFile.ShaHash;
        }

        // Categorize files: unchanged, changed, added, removed
        var changedFiles = new List<FileManifest>();
        var addedFiles = new List<FileManifest>();
        var unchangedCount = 0;

        foreach (var newFile in newManifest.FileManifestList.Elements)
        {
            if (oldFileHashes.TryGetValue(newFile.Filename, out var oldHash))
            {
                oldFileHashes.Remove(newFile.Filename);
                var newHash = BitConverter.ToString(newFile.ShaHash);
                var oldHashStr = BitConverter.ToString(oldHash);
                if (newHash == oldHashStr)
                {
                    unchangedCount++;
                }
                else
                {
                    changedFiles.Add(newFile);
                }
            }
            else
            {
                addedFiles.Add(newFile);
            }
        }

        // Remaining old files are removed in the new version
        var removedFiles = oldFileHashes.Keys.ToList();

        _logger.Information(
            "PrepareUpdateTasks: {Unchanged} unchanged, {Changed} changed, {Added} added, {Removed} removed files",
            unchangedCount, changedFiles.Count, addedFiles.Count, removedFiles.Count);

        // Queue delete tasks for removed files
        foreach (var removedFile in removedFiles)
        {
            var filePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, removedFile);
            if (File.Exists(filePath))
            {
                _ioQueue.Add(new IoTask()
                {
                    DestinationFilePath = filePath,
                    TaskType = IoTaskType.Delete,
                    Size = 0,
                });
            }
        }

        // Build filtered file manifest list containing only changed + added files
        var filesToDownload = new List<FileManifest>();
        filesToDownload.AddRange(changedFiles);
        filesToDownload.AddRange(addedFiles);

        if (filesToDownload.Count == 0)
        {
            _logger.Information("PrepareUpdateTasks: No files need updating");
            return;
        }

        await _downloadManager.InitializeMirrors(gameData.BaseUrls);
        GetChunksToDownloadFiltered(newManifest, filesToDownload);
    }

    /// <summary>
    /// Verify installed files and re-download only broken/missing ones
    /// </summary>
    private async Task PrepareRepairTasks(Game gameData, Manifest manifest)
    {
        _logger.Information("PrepareRepairTasks: Verifying files for {AppName}", CurrentInstall.AppName);

        var invalidFiles = await VerifyFiles(CurrentInstall.Location, manifest.FileManifestList.Elements);

        if (invalidFiles.Count == 0)
        {
            _logger.Information("PrepareRepairTasks: All files valid, nothing to repair");
            return;
        }

        _logger.Information("PrepareRepairTasks: {Count} files need repair", invalidFiles.Count);

        await _downloadManager.InitializeMirrors(gameData.BaseUrls);
        GetChunksToDownloadFiltered(manifest, invalidFiles);
    }

    /// <summary>
    /// Queue chunks to download for only a filtered set of file manifests (used by updates)
    /// </summary>
    private void GetChunksToDownloadFiltered(Manifest data, List<FileManifest> fileManifests)
    {
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
                var filePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, fileManifest.Filename);
                EnsureDirectoryExists(filePath);
                File.Create(filePath).Dispose();
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
            PublishProgressUpdated(CurrentInstall);
        }
    }

    private async Task HandleInstallationStoppage(string errorMessage, Exception? exception = null)
    {
        _ioQueue = new();
        _downloadQueue = new();

        if (CurrentInstall == null)
        {
            _logger.Error("HandleInstallationStoppage called with no active install: {ErrorMessage}", errorMessage);
            ProcessNext();
            return;
        }

        if (!_userCancelled)
        {
            // propage cancelling status if not done already
            await _cancellationTokenSource.CancelAsync();
            CurrentInstall.Status = ActionStatus.Cancelling;
            PublishStatusChanged(CurrentInstall);

            await WaitForInstallWorkersAsync();

            CurrentInstall.Status = ActionStatus.Failed;
            CurrentInstall.StatusMessage ??= errorMessage;
            CurrentInstall.Failure ??= _stoppageFailure ?? OperationFailureClassifier.Classify(exception);
            _installHistory.Add(CurrentInstall);
            PublishStatusChanged(CurrentInstall);
            _logger.Error("Installation failed: {ErrorMessage}", errorMessage);
        }
        else
        {
            CurrentInstall.Status = ActionStatus.Cancelling;
            PublishStatusChanged(CurrentInstall);

            await WaitForInstallWorkersAsync();

            // Clean up only the temp chunk directory, not the entire game folder
            var tempChunkDir = Path.Combine(CurrentInstall.Location, ".Crimson");
            try
            {
                if (Directory.Exists(tempChunkDir))
                    Directory.Delete(tempChunkDir, true);
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to clean up temporary installation files: {ErrorType}", ex.GetType().Name);
                ReportSecondaryFailure(CurrentInstall, new OperationFailure(OperationFailureKind.Storage,
                    "temporary_cleanup_failed", UiStrings.TheOperationWasCancelledButTemporaryFilesCouldNotBeRemoved,
                    UiStrings.CloseApplicationsUsingTheInstallationFolderAndRetryCleanup, false));
            }

            CurrentInstall.Status = ActionStatus.Cancelled;
            _installHistory.Add(CurrentInstall);
            PublishStatusChanged(CurrentInstall);
            _logger.Information("Installation cancelled");
        }
        var state = new InstallManagerState
        {
            CurrentInstall = null,
            IoQueue = null,
            CompletedChunks = null
        };

        var json = JsonSerializer.Serialize(state, _jsonSerializerOptions);
        // Keep ownership until pending-state persistence completes.
        if (!TrySaveStoppedState(CurrentInstall, json)) return;
        CurrentInstall = null;

        ProcessNext();
    }

    private bool TrySaveStoppedState(InstallItem install, string json)
    {
        try
        {
            _storage.SaveInstallState(json);
            return true;
        }
        catch (Exception exception)
        {
            var failure = OperationFailureClassifier.Classify(exception) with
            {
                Code = "operation_state_save_failed",
                Message = UiStrings.CrimsonCouldNotSaveTheOperationSRecoveryState,
                SuggestedAction = UiStrings.RestoreFreeSpaceAndFolderAccessThenRestartCrimsonAndReviewPendingOperations,
                CanRetry = false
            };
            _recoveryStateFailure = failure;
            ReportError(install, failure, OperationPhase.Finalizing);
            return false;
        }
    }

    private async Task WaitForInstallWorkersAsync()
    {
        if (_downloadTasks != null) await Task.WhenAll(_downloadTasks);
        if (_installTasks != null) await Task.WhenAll(_installTasks);
    }

    /// <summary>
    ///  Retrieve the Total Size to download and as well as space for install
    /// </summary>
    /// <param name="appName"></param>
    /// <returns></returns>
    public async Task<(double totalDownloadSizeMb, double totalWriteSizeMb)> GetGameDownloadInstallSizes(
        string appName,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("GetGameDownloadInstallSizes: Getting game manifest for {AppName}", appName);

        var manifestData = await GetManifestDataWithCaching(appName, cancellationToken);

        _logger.Information("GetGameDownloadInstallSizes: Parsing game manifest for {AppName}", appName);
        var manifest = Manifest.ReadAll(manifestData);
        var (totalDownloadSizeBytes, totalWriteSizeBytes) = CalculateManifestSizes(manifest);
        _logger.Information("GetGameDownloadInstallSizes: Download size {DownloadBytes} bytes and write size {WriteBytes} bytes",
            totalDownloadSizeBytes, totalWriteSizeBytes);
        return (totalDownloadSizeBytes, totalWriteSizeBytes);
    }

    public async Task<long?> GetCachedInstallSizeBytes(string appName, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = _libraryManager.GetGameInfo(appName);
            if (game?.LocalAppState is { InstallSize: > 0, InstallStatus: not InstallState.NotInstalled } state)
                return state.InstallSize;
            var version = GetSizeManifestVersion(game);
            if (string.IsNullOrWhiteSpace(version)) return null;

            var bytes = await _storage.GetCachedManifestBytes(appName, version);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null) return null;
            return await Task.Run(() => Manifest.ReadAll(bytes).FileManifestList.Elements.Sum(file => file.FileSize), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Warning("Cached DLC size is unavailable");
            return null;
        }
    }

    private static string? GetSizeManifestVersion(Game? game)
    {
        var installedVersion = game?.LocalAppState?.Version;
        return !string.IsNullOrWhiteSpace(installedVersion)
            ? installedVersion
            : game?.AssetInfos.Windows.BuildVersion;
    }

    internal static (double DownloadBytes, double WriteBytes) CalculateManifestSizes(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var addedChunkGuids = new HashSet<BigInteger>();
        double downloadBytes = 0;
        double writeBytes = 0;

        foreach (var fileManifest in manifest.FileManifestList.Elements)
        {
            foreach (var chunkPart in fileManifest.ChunkParts)
            {
                if (addedChunkGuids.Add(chunkPart.GuidNum))
                    downloadBytes += manifest.CDL.GetChunkByGuidNum(chunkPart.GuidNum).FileSize;
            }

            writeBytes += fileManifest.FileSize;
        }

        return (downloadBytes, writeBytes);
    }

    private async Task<byte[]> GetManifestDataWithCaching(
        string appName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] manifestData = null;
        var gameData = _libraryManager.GetGameInfo(appName);

        // Check for cached manifest
        var localAppState = _storage.LocalAppStateDictionary.FirstOrDefault(game => game.Key == appName).Value;
        var useCache = localAppState != null && gameData.BaseUrls != null &&
                        gameData.AssetInfos.Windows.BuildVersion == localAppState.CachedManifestVersion;

        if (useCache)
        {
            manifestData = await _storage.GetCachedManifestBytes(appName, gameData.AssetInfos.Windows.BuildVersion);
        }

        // If we don't have manifest data yet (cache miss or not using cache)
        if (manifestData == null || manifestData.Length < 1)
        {
            // Get URLs from repository
            var urlData = await _repository.GetManifestUrls(
                gameData.AssetInfos.Windows.Namespace,
                gameData.AssetInfos.Windows.CatalogItemId,
                gameData.AppName,
                cancellationToken: cancellationToken);

            if (urlData == null)
            {
                _logger.Error("GetGameManifest: Failed to get manifest URLs for {AppName}", appName);
                throw new Exception("Cannot fetch manifest data");
            }

            gameData.BaseUrls = urlData.BaseUrls;
            _storage.SaveMetaData(gameData);

            // Download the manifest and cache it
            manifestData = await _repository.GetGameManifest(urlData, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.CacheManifestBytes(appName, gameData.AssetInfos.Windows.BuildVersion, manifestData);
            cancellationToken.ThrowIfCancellationRequested();

            if (localAppState == null)
            {
                localAppState = new LocalAppState()
                {
                    AppName = appName,
                    CachedManifestVersion = gameData.AssetInfos.Windows.BuildVersion,
                    InstallStatus = InstallState.NotInstalled
                };
            }
            else
            {
                localAppState.CachedManifestVersion = gameData.AssetInfos.Windows.BuildVersion;
            }
            _storage.AddToLocalAppState(appName, localAppState);
        }

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
            _ = StopProcessing();

        var queuedItems = _installQueue.Where(item => item.AppName == appName).ToList();
        _installQueue.RemoveAll(item => item.AppName == appName);
        foreach (var item in queuedItems)
        {
            item.Status = ActionStatus.Cancelled;
            PublishStatusChanged(item);
        }
        if (queuedItems.Count > 0)
            _logger.Information("RemoveFromQueue: Removed {AppName} from the install queue", appName);
    }

    public List<string> GetQueueItemNames()
    {
        return _installQueue.Select(item => item.AppName).ToList();
    }

    public IReadOnlyList<OperationSnapshot> GetQueueOperations() =>
        _installQueue.Select(OperationSnapshot.From).ToList();

    public OperationSnapshot? GetActiveOperationForGame(string appName)
    {
        var current = CurrentOperation;
        if (current is { IsActive: true } &&
            _libraryManager.IsOperationForGame(current.AppName, appName))
            return current;

        return GetQueueOperations().FirstOrDefault(operation =>
            operation.IsActive &&
            _libraryManager.IsOperationForGame(operation.AppName, appName));
    }

    public IReadOnlyList<OperationSnapshot> GetHistoryOperations()
    {
        var seen = new HashSet<string>();
        var result = new List<OperationSnapshot>();
        for (var index = _installHistory.Count - 1; index >= 0; index--)
        {
            if (seen.Add(_installHistory[index].AppName))
                result.Add(OperationSnapshot.From(_installHistory[index]));
        }
        result.Reverse();
        return result;
    }

    public bool RetryFailedOperation(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName) || GameGameInQueue(appName) is not null)
            return false;

        var failed = _installHistory.LastOrDefault(item => item.AppName == appName);
        if (failed?.Status != ActionStatus.Failed || failed.Failure?.CanRetry != true)
            return false;

        AddToQueue(new InstallItem(failed.AppName, failed.Action, failed.Location)
        {
            MoveLocation = failed.MoveLocation,
            EpicImportSource = failed.EpicImportSource,
            EpicImportOwner = failed.EpicImportOwner,
            EpicImportAccountGeneration = failed.EpicImportAccountGeneration,
            CreateDesktopShortcut = failed.CreateDesktopShortcut,
            CreateStartMenuShortcut = failed.CreateStartMenuShortcut
        });
        return true;
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

    public Task StopProcessing()
    {
        InstallItem? install;
        lock (_installItemLock)
        {
            install = CurrentInstall;
            if (install is null)
            {
                _logger.Warning("StopProcessing: no active install to cancel");
                return Task.CompletedTask;
            }
            if (_isFinalizing)
            {
                _logger.Information("StopProcessing: finalization has started for {AppName}", install.AppName);
                return Task.CompletedTask;
            }

            install.Status = ActionStatus.Cancelling;
            _userCancelled = true;
        }

        _pauseEvent.Set();
        RequestStoppage("Cancel install");
        PublishStatusChanged(install);
        return Task.CompletedTask;
    }

    private string GetStoppageReason() => Volatile.Read(ref _stoppageReason) ?? "Cancel install";

    private void RequestStoppage(string errorMessage, Exception? exception = null)
    {
        if (exception is not null)
            Interlocked.CompareExchange(ref _stoppageFailure, OperationFailureClassifier.Classify(exception), null);
        Interlocked.CompareExchange(ref _stoppageReason, errorMessage, null);
        if (!_cancellationTokenSource.IsCancellationRequested)
            _cancellationTokenSource.Cancel();
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
            PublishStatusChanged(CurrentInstall);

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

        // Workers are only absent after a restart (LoadPendingInstalls); in that case
        // the pipeline has to be rebuilt. Otherwise the original workers are still
        // alive and parked on _pauseEvent, so releasing them is enough. Calling
        // ProcessNext here would spawn a second set of workers over the same queues
        // and re-invoke _downloadQueue.CompleteAdding().
        if (_downloadTasks is null || _installTasks is null)
        {
            ProcessNext(true);
            return;
        }

        _installStopWatch.Start();
        CurrentInstall.Status = ActionStatus.Processing;
        _pauseEvent.Set();
        PublishStatusChanged(CurrentInstall);
    }

    public async Task LoadPendingInstalls()
    {
        if (CurrentInstall is not null) return;
        var state = ReadPendingInstallState();
        if (state?.CurrentInstall is not { } pending) return;

        var restored = new InstallItem(pending.AppName, pending.Action, pending.Location)
        {
            CreateDesktopShortcut = pending.CreateDesktopShortcut,
            CreateStartMenuShortcut = pending.CreateStartMenuShortcut,
            EpicImportSource = pending.EpicImportSource,
            EpicImportOwner = pending.EpicImportOwner,
            Notice = pending.Notice,
            Status = ActionStatus.Paused,
            Phase = OperationPhase.Preparing
        };
        if (!_libraryManager.TryQueueOperation(OperationSnapshot.From(restored), () => CurrentInstall = restored,
            excludeRelatedOperations: restored.EpicImportSource is not null)) return;
        try
        {
            if (restored.EpicImportSource is not null) await RestoreEpicImportAccountAsync(restored);
            state.IoQueue?.ForEach(task => _ioQueue.Add(task));
            var completedChunks = state.CompletedChunks ?? [];
            completedChunks.ForEach(chunk => _completedChunks.Add(chunk));
            await PrepareTasks(true, completedChunks);
            restored.Status = ActionStatus.Paused;
            PublishStatusChanged(restored);
            _pauseEvent.Set();
        }
        catch (Exception exception)
        {
            restored.Status = ActionStatus.Failed;
            restored.Failure = OperationFailureClassifier.Classify(exception);
            _installHistory.Add(restored);
            ResetQueues();
            PublishStatusChanged(restored);
            CurrentInstall = null;
        }
    }

    private async Task RestoreEpicImportAccountAsync(InstallItem install)
    {
        var generation = _storage.AccountGeneration;
        await _libraryManager.GetLibraryData();
        var owner = await GetEpicImportOwnerAsync();
        lock (_storage.AccountDataLock)
        {
            if (_storage.AccountGeneration != generation || owner is null || install.EpicImportOwner != owner)
                throw new InvalidDataException("This import belongs to a different account. Start a new import.");
            install.EpicImportAccountGeneration = generation;
        }
    }

    private InstallManagerState? ReadPendingInstallState()
    {
        string jsonData;
        try
        {
            jsonData = _storage.GetInstallState();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ReportPreparationFailure(string.Empty, ActionType.Install, exception);
            return null;
        }
        if (string.IsNullOrWhiteSpace(jsonData)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallManagerState>(jsonData, _jsonSerializerOptions);
        }
        catch (JsonException)
        {
            ReportPreparationFailure(string.Empty, ActionType.Install, new OperationFailure(
                OperationFailureKind.Manifest, "pending_state_invalid", UiStrings.ThePendingOperationCouldNotBeRestored,
                UiStrings.StartTheOperationAgainFromTheLibrary, false));
            return null;
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
    public long DestinationFileSize { get; set; }
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
