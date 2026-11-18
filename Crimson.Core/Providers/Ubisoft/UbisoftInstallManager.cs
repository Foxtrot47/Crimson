using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers.Ubisoft.Protocol;
using Crimson.Resources;
using Microsoft.Win32;
using Serilog;

namespace Crimson.Providers.Ubisoft;

/// <summary>Registry values and prerequisites are applied behind one elevation prompt per operation.</summary>
public sealed class UbisoftInstallManager : ILauncherInstaller, IInstallLanguageSource, IInstallImportSource
{
    private const int HistoryLimit = 50;
    private const long MaximumManifestBytes = 512L * 1024 * 1024;
    private const long MaximumMetadataBytes = 16L * 1024 * 1024;
    private const string PendingFile = "pending.json";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly IUbisoftProductSource _products;
    private readonly UbisoftInstalledStore _store;
    private readonly IUbisoftSystemSetup _setup;
    private readonly UbisoftContentConnector _connect;
    private readonly IUbisoftCdn _cdn;
    private readonly IUbisoftClientRegistry _registry;
    private readonly string _providerRoot;
    private readonly string? _launcherRuntimeDirectory;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly List<InstallItem> _queue = [];
    private readonly List<InstallItem> _history = [];
    private Job? _job;
    private bool _workerActive;
    private Task _worker = Task.CompletedTask;
    private int _pendingLoaded;

    public UbisoftInstallManager(UbisoftAuthentication auth, IUbisoftProductSource products, UbisoftInstalledStore store,
        IUbisoftSystemSetup systemSetup, ILogger log)
        : this(products, store, systemSetup,
            (productId, ct) => UbisoftDemuxContentSource.OpenAsync(auth, productId, ct),
            new UbisoftCdnPool(), new UbisoftWindowsClientRegistry(), store.ProviderRoot,
            Directory.Exists(UbisoftRuntimeFiles.BundledDirectory) ? UbisoftRuntimeFiles.BundledDirectory : null, log) { }

    internal UbisoftInstallManager(IUbisoftProductSource products, UbisoftInstalledStore store, IUbisoftSystemSetup systemSetup,
        UbisoftContentConnector connect, IUbisoftCdn cdn, IUbisoftClientRegistry registry, string providerRoot,
        string? launcherRuntimeDirectory, ILogger log)
    {
        _products = products;
        _store = store;
        _setup = systemSetup;
        _connect = connect;
        _cdn = cdn;
        _registry = registry;
        _providerRoot = Path.GetFullPath(providerRoot);
        _launcherRuntimeDirectory = launcherRuntimeDirectory;
        _log = log.ForContext<UbisoftInstallManager>();
    }

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public event Action<InstallItem>? InstallationStatusChanged;
    public event Action<InstallItem>? InstallProgressUpdate;
    public event Action<OperationSnapshot>? OperationUpdated;
    public event Action<OperationSnapshot>? TransferRatesUpdated;
    public event Action<OperationSnapshot>? OperationError;

    public bool SupportsPause => true;
    public bool SupportsShortcuts => false;

    public InstallItem? CurrentInstall
    {
        get { lock (_sync) return _job?.Item; }
    }

    public OperationSnapshot? CurrentOperation => CurrentInstall is { } item ? OperationSnapshot.From(item) : null;

    public bool Supports(ActionType action) => action is ActionType.Install or ActionType.Update or ActionType.Repair
        or ActionType.Verify or ActionType.Uninstall or ActionType.Import or ActionType.Move;

    public IReadOnlyList<OperationSnapshot> GetQueueOperations()
    {
        lock (_sync) return _queue.Select(OperationSnapshot.From).ToList();
    }

    public IReadOnlyList<OperationSnapshot> GetHistoryOperations()
    {
        lock (_sync) return _history.Select(OperationSnapshot.From).ToList();
    }

    public List<string> GetQueueItemNames()
    {
        lock (_sync) return _queue.Select(item => item.AppName).ToList();
    }

    public List<string> GetHistoryItemsNames()
    {
        lock (_sync) return _history.Select(item => item.AppName).ToList();
    }

    public OperationSnapshot? GetActiveOperationForGame(string appName)
    {
        lock (_sync)
        {
            var active = _job?.Item is { } current && Same(current.AppName, appName)
                ? current : _queue.FirstOrDefault(item => Same(item.AppName, appName));
            return active is null ? null : OperationSnapshot.From(active);
        }
    }

    public bool TryAddToQueue(InstallItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Supports(item.Action) || !TryProductId(item.AppName, out _)) return false;
        lock (_sync)
        {
            // A job that already reported its outcome is only winding down; the worker picks the new item next.
            if (_job?.Item is { } current && Same(current.AppName, item.AppName) && !IsFinished(current.Status)) return false;
            if (_queue.Any(queued => Same(queued.AppName, item.AppName))) return false;
            item.Status = ActionStatus.Pending;
            item.Phase = OperationPhase.Queued;
            item.Failure = null;
            _queue.Add(item);
        }
        Publish(item);
        StartWorker();
        return true;
    }

    public void AddToQueue(InstallItem item)
    {
        if (!TryAddToQueue(item)) _log.Debug("Ubisoft: {Game} is already queued or running", item.AppName);
    }

    public void CancelInstall(string appName)
    {
        InstallItem? queued;
        Job? job = null;
        var started = false;
        lock (_sync)
        {
            queued = _queue.FirstOrDefault(item => Same(item.AppName, appName));
            if (queued is not null) _queue.Remove(queued);
            else if (_job is { } current && Same(current.Item.AppName, appName))
            {
                job = current;
                started = current.Started;
                if (!started) _job = null;
            }
        }
        if (queued is not null)
        {
            Complete(queued, ActionStatus.Cancelled, null);
            return;
        }
        if (job is not { } cancelled) return;
        if (started) CancelRunning(cancelled);
        else _ = Task.Run(() => CancelRestoredAsync(cancelled));
    }

    private void CancelRunning(Job job)
    {
        job.Item.Status = ActionStatus.Cancelling;
        Publish(job.Item);
        job.Cancellation.Cancel();
    }

    /// <summary>A job restored paused that never started: no worker owns it, so its cleanup runs here.</summary>
    private async Task CancelRestoredAsync(Job restored)
    {
        restored.Item.Status = ActionStatus.Cancelling;
        Publish(restored.Item);
        await CleanupCancelledAsync(restored.Item);
        DeletePending();
        Complete(restored.Item, ActionStatus.Cancelled, null);
        StartWorker();
    }

    /// <summary>
    /// Only transfers and verification wait on the pause gate. Phase and status change together under
    /// <c>_sync</c> (see <see cref="EnterFinalizing"/>), so a pause can never land on a phase that ignores it.
    /// </summary>
    public void PauseInstall()
    {
        Job? job;
        lock (_sync)
        {
            job = _job;
            if (job is not { Started: true } || job.Item.Status is not (ActionStatus.OnGoing or ActionStatus.Processing) ||
                job.Item.Phase is not (OperationPhase.Transferring or OperationPhase.Verifying) ||
                job.Item.Action is not (ActionType.Install or ActionType.Update or ActionType.Repair or ActionType.Verify)) return;
            job.Pause.Pause();
            job.Item.Status = ActionStatus.Paused;
        }
        job.Item.TransferRates.SetTransferring(false);
        job.Item.DownloadSpeedRawMiB = 0;
        job.Item.WriteSpeedMiB = 0;
        Publish(job.Item);
        _log.Information("Ubisoft: paused {Game}", job.Item.AppName);
    }

    /// <summary>
    /// Finalizing (registry, system setup, records) cannot pause. A pause that arrived as the last transfer
    /// finished is withdrawn instead of leaving a "paused" operation that keeps running.
    /// </summary>
    private void EnterFinalizing(Job job)
    {
        lock (_sync)
        {
            job.Item.Phase = OperationPhase.Finalizing;
            if (job.Item.Status == ActionStatus.Paused) job.Item.Status = ActionStatus.Processing;
            job.Pause.Resume();
        }
        job.Item.TransferRates.SetTransferring(false);
        Publish(job.Item);
    }

    private void EnterGatedPhase(Job job, OperationPhase phase)
    {
        lock (_sync)
        {
            job.Item.Phase = phase;
            if (job.Item.Status != ActionStatus.Paused) job.Item.Status = ActionStatus.Processing;
        }
    }

    public void ResumeInstall()
    {
        Job? job;
        lock (_sync) job = _job;
        if (job?.Item.Status != ActionStatus.Paused) return;
        if (!job.Started)
        {
            StartWorker(resume: true);
            return;
        }
        job.Item.Status = ActionStatus.Processing;
        job.Item.TransferRates.SetTransferring(job.Item.Phase == OperationPhase.Transferring);
        job.Pause.Resume();
        Publish(job.Item);
    }

    /// <summary>Stops the running job for shutdown. Its files and resume state stay; it comes back paused next start.</summary>
    public async Task StopProcessing()
    {
        Job? job;
        Task worker;
        lock (_sync)
        {
            job = _job;
            worker = _worker;
        }
        if (job is { Started: true })
        {
            job.ShuttingDown = true;
            job.Cancellation.Cancel();
        }
        try { await worker; }
        catch (OperationCanceledException) { }
    }

    public async Task LoadPendingInstalls()
    {
        if (Interlocked.Exchange(ref _pendingLoaded, 1) == 1) return;
        await _store.LoadAsync();
        var file = Path.Combine(_providerRoot, PendingFile);
        if (!File.Exists(file)) return;
        PendingOperation? pending;
        try { pending = JsonSerializer.Deserialize<PendingOperation>(await File.ReadAllTextAsync(file)); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            DeletePending();
            return;
        }
        if (pending is null || !TryProductId(pending.AppName, out _) || !Supports(pending.Action))
        {
            DeletePending();
            return;
        }
        var item = new InstallItem(pending.AppName, pending.Action, pending.Location)
        {
            MoveLocation = pending.MoveLocation!,
            Languages = pending.Languages,
            Status = ActionStatus.Paused,
            Phase = OperationPhase.Preparing,
            CreatedTime = DateTime.UtcNow
        };
        lock (_sync)
        {
            if (_job is not null || _queue.Any(queued => Same(queued.AppName, item.AppName))) return;
            _job = new Job(item);
        }
        Publish(item);
    }

    public bool RetryFailedOperation(string appName)
    {
        InstallItem? failed;
        lock (_sync)
            failed = _history.FirstOrDefault(item => Same(item.AppName, appName) &&
                item.Status is ActionStatus.Failed or ActionStatus.Cancelled);
        return failed is not null && TryAddToQueue(new InstallItem(failed.AppName, failed.Action, failed.Location)
        {
            MoveLocation = failed.MoveLocation,
            Languages = failed.Languages
        });
    }

    public void ReportPreparationFailure(string appName, ActionType action, OperationFailure failure) =>
        OperationError?.Invoke(OperationSnapshot.From(new InstallItem(appName, action, "")
        {
            Status = ActionStatus.Failed,
            Phase = OperationPhase.Preparing,
            Failure = failure
        }));

    public void ReportPreparationFailure(string appName, ActionType action, Exception exception) =>
        ReportPreparationFailure(appName, action, FailureFor(exception));

    public void ReportSecondaryFailure(InstallItem install, OperationFailure failure)
    {
        install.Notice = failure;
        OperationError?.Invoke(OperationSnapshot.From(install) with { Failure = failure, ShowErrorDialog = false });
        InstallProgressUpdate?.Invoke(install);
        OperationUpdated?.Invoke(OperationSnapshot.From(install));
    }

    public async Task<(double DownloadBytes, double InstallBytes)> GetGameDownloadInstallSizes(string appName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var productId = ProductId(appName);
            var choice = await LanguageContextAsync(productId, cancellationToken);
            if (choice is null) return (0, 0);
            var languages = choice.Value.Installed?.Languages is { Count: > 0 } current
                ? current : [UbisoftInstallSelection.DefaultLanguage(choice.Value.Available, CultureInfo.CurrentUICulture.Name)];
            return choice.Value.Metadata.Estimate(languages, choice.Value.Owned);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.Information("Ubisoft: sizes for {Game} are unavailable ({Error})", appName, error.GetType().Name);
            return (0, 0);
        }
    }

    public async Task<InstallLanguageChoice?> GetInstallLanguages(string appName, CancellationToken cancellationToken = default)
    {
        var productId = ProductId(appName);
        var context = await LanguageContextAsync(productId, cancellationToken);
        if (context is not { } choice || choice.Available.Count == 0) return null;
        // A build that forbids changing language keeps the one it was installed with.
        if (choice.Installed is not null && choice.Config is { ChangeLanguageEnabled: false }) return null;
        var selected = choice.Installed?.Languages
            .Where(code => choice.Available.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected is not { Count: > 0 })
            selected = [UbisoftInstallSelection.DefaultLanguage(choice.Available, CultureInfo.CurrentUICulture.Name)];
        return new InstallLanguageChoice(
            choice.Available.Select(code => new InstallLanguageOption(code, LanguageName(code))).ToList(),
            selected, AllowsMultiple: true);
    }

    public async Task<(double DownloadBytes, double InstallBytes)> GetSizesForLanguages(string appName,
        IReadOnlyList<string> languages, CancellationToken cancellationToken = default)
    {
        var productId = ProductId(appName);
        var context = await LanguageContextAsync(productId, cancellationToken);
        if (context is not { } choice) return (0, 0);
        return choice.Metadata.Estimate(languages, choice.Owned);
    }

    public async Task<IReadOnlyList<ImportCandidate>> DiscoverImportCandidates(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
        var candidates = new List<ImportCandidate>();
        foreach (var (productId, install) in _registry.ReadInstalls())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_store.Find(productId) is not null) continue;
            string path;
            try { path = UbisoftInstalledStore.NormalizePath(install.InstallDir.Replace('/', '\\')); }
            catch (ArgumentException) { continue; }
            if (!Directory.Exists(path)) continue;
            var product = await _products.GetProduct(productId, cancellationToken);
            if (product is not { Owned: true } || product.ProductType != UbisoftOwnedProduct.TypeGame) continue;
            candidates.Add(new ImportCandidate(AppName(productId),
                _products.GetTitle(productId) ?? "Ubisoft " + productId.ToString(CultureInfo.InvariantCulture), path));
        }
        return candidates;
    }

    public async Task<OperationFailure?> ValidateImportLocation(string appName, string installPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var productId = ProductId(appName);
            if (string.IsNullOrWhiteSpace(installPath) || !Path.IsPathFullyQualified(installPath) || !Directory.Exists(installPath))
                return new UbisoftInstallUnavailableException("folder_missing").ToFailure();
            var root = UbisoftInstalledStore.NormalizePath(installPath);
            var product = await RequireInstallableAsync(productId, cancellationToken);
            await using var source = new UbisoftReconnectingSource(_connect, productId);
            var manifest = await LoadManifestAsync(source, product.LatestManifest!, cancellationToken);
            var owned = await OwnedForAsync(productId, product, cancellationToken);
            var languages = ResolveLanguages(null, ClientLanguage(productId, root),
                UbisoftInstallSelection.AvailableLanguages(manifest, owned));
            var files = UbisoftInstallSelection.Select(manifest, languages, owned);
            return ContainsAnyFile(root, files) ? null : new UbisoftInstallUnavailableException("import_not_found").ToFailure();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return FailureFor(error);
        }
    }

    /// <summary>
    /// The launcher InstallDir is set here rather than at install time because Ubisoft Connect rewrites it
    /// whenever it runs.
    /// </summary>
    public async Task<bool> PrepareLaunchAsync(uint productId, CancellationToken ct)
    {
        await _store.LoadAsync(ct);
        if (_store.Find(productId) is not { } installed) return true;
        if (IsRunning(productId)) return false;
        try
        {
            var manifest = await PendingSetupManifestAsync(installed, ct);
            var updated = await ApplySetupAsync(null, installed, manifest, includeRuns: installed.SystemSetupPending, ct,
                _launcherRuntimeDirectory);
            if (updated != installed) await _store.SaveAsync(updated, ct);
            return !updated.SystemSetupPending;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.Warning("Ubisoft: launch setup for {Product} failed ({Error})", productId, error.GetType().Name);
            return false;
        }
    }

    private bool IsRunning(uint productId)
    {
        lock (_sync)
            return _job?.Item is { } current && TryProductId(current.AppName, out var running) && running == productId;
    }

    private async Task<UbisoftManifest?> PendingSetupManifestAsync(UbisoftInstalledGame installed, CancellationToken ct)
    {
        if (!installed.SystemSetupPending || installed.ManifestId is not { } manifestId || !IsManifestId(manifestId))
            return null;
        await using var source = new UbisoftReconnectingSource(_connect, installed.ProductId);
        return await LoadManifestAsync(source, manifestId, ct);
    }

    private sealed class Job(InstallItem item)
    {
        public InstallItem Item { get; } = item;
        public CancellationTokenSource Cancellation { get; } = new();
        public UbisoftPauseGate Pause { get; } = new();
        public bool Started { get; set; }
        public bool ShuttingDown { get; set; }
        public InstallItem? FollowUp { get; set; }
        public long Downloaded;
        public long Written;
        public long LastReport;
        public Timer? Sampler;
    }

    private sealed record PendingOperation(string AppName, ActionType Action, string Location, string? MoveLocation,
        IReadOnlyList<string>? Languages);

    private void StartWorker(bool resume = false)
    {
        lock (_sync)
        {
            if (_workerActive) return;
            if (_job is { Started: false } && !resume) return;
            if (_job is null && _queue.Count == 0) return;
            _workerActive = true;
            _worker = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            Job job;
            lock (_sync)
            {
                if (_job is { Started: false } restored) job = restored;
                else if (_job is null && _queue.Count > 0)
                {
                    job = new Job(_queue[0]);
                    _queue.RemoveAt(0);
                    _job = job;
                }
                else
                {
                    _workerActive = false;
                    return;
                }
                job.Started = true;
            }
            var shuttingDown = false;
            try
            {
                await RunJobAsync(job);
                shuttingDown = job.ShuttingDown;
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_job, job)) _job = null;
                    if (shuttingDown) _workerActive = false;
                }
                job.Cancellation.Dispose();
            }
            if (shuttingDown) return;
            if (job.FollowUp is { } followUp) TryAddToQueue(followUp);
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var item = job.Item;
        item.Status = ActionStatus.OnGoing;
        item.Phase = OperationPhase.Preparing;
        item.Failure = null;
        if (item.CreatedTime == default) item.CreatedTime = DateTime.UtcNow;
        Publish(item);
        var keepPending = false;
        try
        {
            if (item.Action is ActionType.Install or ActionType.Update or ActionType.Repair) SavePending(item);
            await ExecuteAsync(job);
            Complete(item, ActionStatus.Success, null);
        }
        catch (Exception) when (job.ShuttingDown)
        {
            // Shutdown: the progress file and pending record let the next start resume.
            keepPending = true;
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            await CleanupCancelledAsync(item);
            Complete(item, ActionStatus.Cancelled, null);
        }
        catch (Exception error)
        {
            var failure = FailureFor(error);
            _log.Warning("Ubisoft {Action} of {Game} failed: {Reason} ({Error})", item.Action, item.AppName, failure.Code,
                error.GetType().Name);
            Complete(item, ActionStatus.Failed, failure);
        }
        finally
        {
            StopSampling(job);
            if (!keepPending) DeletePending();
        }
    }

    private Task ExecuteAsync(Job job)
    {
        var item = job.Item;
        var productId = ProductId(item.AppName);
        return item.Action switch
        {
            ActionType.Install => InstallAsync(job, productId),
            ActionType.Update => UpdateAsync(job, productId),
            ActionType.Repair => RepairAsync(job, productId, verifyOnly: false),
            ActionType.Verify => RepairAsync(job, productId, verifyOnly: true),
            ActionType.Uninstall => UninstallAsync(job, productId),
            ActionType.Import => ImportAsync(job, productId),
            ActionType.Move => MoveAsync(job, productId),
            _ => throw new NotSupportedException()
        };
    }

    private async Task InstallAsync(Job job, uint productId)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        await _store.LoadAsync(ct);
        if (_store.Find(productId) is not null)
        {
            // Installing what is already installed means bringing it back to its manifest.
            await RepairAsync(job, productId, verifyOnly: false);
            return;
        }
        var product = await RequireInstallableAsync(productId, ct);
        if (string.IsNullOrWhiteSpace(item.Location) || !Path.IsPathFullyQualified(item.Location))
            throw new UbisoftInstallUnavailableException("folder_missing");
        var root = UbisoftInstalledStore.NormalizePath(item.Location);
        item.Location = root;
        EnsureInstallFolder(root, productId);
        Directory.CreateDirectory(root);

        await using var source = new UbisoftReconnectingSource(_connect, productId);
        var manifestId = product.LatestManifest!;
        var manifest = await LoadManifestAsync(source, manifestId, ct);
        var owned = await OwnedForAsync(productId, product, ct);
        var languages = ResolveLanguages(item.Languages, null, UbisoftInstallSelection.AvailableLanguages(manifest, owned));
        var files = UbisoftInstallSelection.Select(manifest, languages, owned);

        await TransferAsync(job, source, manifest, manifestId, root, files, fresh: true, ct);

        EnterFinalizing(job);
        var record = new UbisoftInstalledGame
        {
            ProductId = productId,
            InstallPath = root,
            ManifestId = manifestId,
            GameVersion = manifest.GameVersion,
            Languages = languages,
            ChunkIds = ChunkIds(manifest, languages, owned),
            SystemSetupPending = true,
            InstallBytes = UbisoftInstallFiles.Bytes(files),
            InstalledAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(record, CancellationToken.None);
        UbisoftInstallProgress.Delete(root);
        record = await ApplySetupAsync(item, record, manifest, includeRuns: true, CancellationToken.None);
        await _store.SaveAsync(record, CancellationToken.None);
        _log.Information("Ubisoft: installed {Game} ({Files} files, {MiB} MiB)", item.AppName, files.Count,
            UbisoftInstallFiles.Describe(record.InstallBytes));
    }

    private async Task UpdateAsync(Job job, uint productId)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        var installed = await RequireInstalledAsync(productId, ct);
        var product = await RequireInstallableAsync(productId, ct);
        var root = installed.InstallPath;
        item.Location = root;

        await using var source = new UbisoftReconnectingSource(_connect, productId);
        var manifestId = product.LatestManifest!;
        var manifest = await LoadManifestAsync(source, manifestId, ct);
        var owned = await OwnedForAsync(productId, product, ct);
        var languages = ResolveLanguages(item.Languages, installed.Languages,
            UbisoftInstallSelection.AvailableLanguages(manifest, owned));
        var files = UbisoftInstallSelection.Select(manifest, languages, owned);

        var previous = await PreviousSelectionAsync(source, installed, owned, item.AppName, ct);
        IReadOnlyList<UbisoftManifestFile> changed = previous is null
            ? await VerifyAsync(job, root, files, ct)
            : FilesToTransfer(root, files, previous, UbisoftInstallProgress.Load(root, productId, manifestId));
        if (previous is not null) RemoveTypeTransitions(root, previous, files);

        await TransferAsync(job, source, manifest, manifestId, root, changed, fresh: false, ct);
        EnterFinalizing(job);
        if (previous is not null) UbisoftInstallFiles.DeleteLeaving(root, previous, files, _log);
        var record = WithBuild(installed, manifestId, manifest, languages, owned, files) with { UpdatedAt = DateTimeOffset.UtcNow };
        await _store.SaveAsync(record, CancellationToken.None);
        UbisoftInstallProgress.Delete(root);
        if (previous is not null && !string.Equals(installed.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase))
            await ForgetManifestAsync(installed.ManifestId!);
        record = await ApplySetupAsync(item, record, manifest, includeRuns: true, CancellationToken.None);
        await _store.SaveAsync(record, CancellationToken.None);
        _log.Information("Ubisoft: updated {Game} ({Changed} of {Files} files)", item.AppName, changed.Count, files.Count);
    }

    private async Task<IReadOnlyList<UbisoftManifestFile>?> PreviousSelectionAsync(IUbisoftContentSource source,
        UbisoftInstalledGame installed, IReadOnlySet<uint> owned, string appName, CancellationToken ct)
    {
        if (installed.ManifestId is not { } oldId || !IsManifestId(oldId)) return null;
        try { return InstalledSelection(await LoadManifestAsync(source, oldId, ct), installed, owned); }
        catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.Information("Ubisoft: previous build of {Game} is unavailable ({Error}); checking files instead",
                appName, error.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Files of the new build to download: left partial by an interrupted run, new or changed since
    /// <paramref name="previous"/>, or missing on disk.
    /// </summary>
    private static IReadOnlyList<UbisoftManifestFile> FilesToTransfer(string root, IReadOnlyList<UbisoftManifestFile> files,
        IReadOnlyList<UbisoftManifestFile> previous, UbisoftInstallProgress? progress)
    {
        var old = previous.ToDictionary(file => UbisoftInstallFiles.Key(file.Path), StringComparer.OrdinalIgnoreCase);
        return files.Where(file => progress?.IsComplete(file.Path) == false && progress.Partial.ContainsKey(file.Path) ||
            !old.TryGetValue(UbisoftInstallFiles.Key(file.Path), out var before) ||
            !UbisoftInstallFiles.SameContent(before, file) || !Present(root, file)).ToList();
    }

    /// <summary>
    /// Verify checks every selected file against the manifest. Repair also downloads what failed, applies a
    /// language change (adds the new language's files, removes files only the old one used) and re-applies a
    /// system setup that is still pending.
    /// </summary>
    private async Task RepairAsync(Job job, uint productId, bool verifyOnly)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        var installed = await RequireInstalledAsync(productId, ct);
        var root = installed.InstallPath;
        item.Location = root;
        var product = await _products.GetProduct(productId, ct);
        var manifestId = RepairManifestId(installed, product);

        await using var source = new UbisoftReconnectingSource(_connect, productId);
        var manifest = await LoadManifestAsync(source, manifestId, ct);
        var owned = await OwnedForAsync(productId, product, ct);
        var available = UbisoftInstallSelection.AvailableLanguages(manifest, owned);
        var languages = verifyOnly
            ? ResolveLanguages(installed.Languages, null, available)
            : ResolveLanguages(item.Languages, installed.Languages, available);
        var files = UbisoftInstallSelection.Select(manifest, languages, owned);
        var previous = installed.ManifestId is null ? null : InstalledSelection(manifest, installed, owned);
        if (previous is not null) RemoveTypeTransitions(root, previous, files);

        var damaged = await VerifyAsync(job, root, files, ct);
        var repaired = WithBuild(installed, manifestId, manifest, languages, owned, files);
        if (verifyOnly)
        {
            await RecordVerificationAsync(installed, repaired, damaged.Count);
            return;
        }

        if (damaged.Count > 0) await TransferAsync(job, source, manifest, manifestId, root, damaged, fresh: false, ct);
        EnterFinalizing(job);
        if (previous is not null) UbisoftInstallFiles.DeleteLeaving(root, previous, files, _log);
        var languageChanged = !languages.SequenceEqual(installed.Languages, StringComparer.OrdinalIgnoreCase);
        var record = repaired with
        {
            SystemSetupPending = installed.SystemSetupPending || languageChanged,
            NeedsRepair = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(record, CancellationToken.None);
        UbisoftInstallProgress.Delete(root);
        record = await ApplySetupAsync(item, record, manifest, includeRuns: true, CancellationToken.None);
        await _store.SaveAsync(record, CancellationToken.None);
        _log.Information("Ubisoft: repaired {Game} ({Damaged} of {Files} files replaced)", item.AppName, damaged.Count,
            files.Count);
    }

    /// <summary>The installed build, or the latest one for an import never matched to a build.</summary>
    private static string RepairManifestId(UbisoftInstalledGame installed, UbisoftOwnedProduct? product)
    {
        if (installed.ManifestId is { } current && IsManifestId(current)) return current;
        if (product?.LatestManifest is { } latest && IsManifestId(latest)) return latest;
        throw new UbisoftInstallUnavailableException("no_manifest");
    }

    private async Task RecordVerificationAsync(UbisoftInstalledGame installed, UbisoftInstalledGame verified, int damaged)
    {
        if (damaged > 0)
        {
            // Like Epic's "Broken": the game page offers Repair instead of Play.
            if (!installed.NeedsRepair) await _store.SaveAsync(installed with { NeedsRepair = true }, CancellationToken.None);
            throw new UbisoftVerificationFailedException(damaged);
        }
        if (installed.ManifestId is null || installed.NeedsRepair)
            await _store.SaveAsync(verified with { NeedsRepair = false }, CancellationToken.None);
    }

    private static UbisoftInstalledGame WithBuild(UbisoftInstalledGame installed, string manifestId, UbisoftManifest manifest,
        IReadOnlyList<string> languages, IReadOnlySet<uint> owned, IReadOnlyList<UbisoftManifestFile> files) =>
        installed with
        {
            ManifestId = manifestId,
            GameVersion = manifest.GameVersion ?? installed.GameVersion,
            Languages = languages,
            ChunkIds = ChunkIds(manifest, languages, owned),
            InstallBytes = UbisoftInstallFiles.Bytes(files)
        };

    private async Task UninstallAsync(Job job, uint productId)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        var installed = await RequireInstalledAsync(productId, ct);
        var root = installed.InstallPath;
        item.Location = root;
        InstallPathPolicy.RequireSafeRoot(root);
        item.Phase = OperationPhase.Uninstalling;
        item.Status = ActionStatus.Processing;
        Publish(item);

        var manifest = await ManifestForRemovalAsync(installed, ct);
        if (Directory.Exists(root))
        {
            UbisoftRuntimeFiles.RemoveProvisioned(root);
            if (manifest is not null)
            {
                var owned = await OwnedOrEmptyAsync(installed.ProductId, ct);
                var files = InstalledSelection(manifest, installed, owned);
                UbisoftInstallFiles.Delete(root, files, _log);
            }
            else
                _log.Warning("Ubisoft: no manifest for {Game}; only Crimson's own files are removed", item.AppName);
            var crimson = Path.Combine(root, UbisoftInstallProgress.CrimsonFolder);
            if (Directory.Exists(crimson) && (File.GetAttributes(crimson) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(crimson, recursive: true);
            InstallPathPolicy.RemoveEmptyDirectories(root, []);
        }

        await _store.RemoveAsync(productId, CancellationToken.None);
        var lastGame = (await _store.LoadAsync(CancellationToken.None)).Count == 0;
        if (installed.ManifestId is { } manifestId && IsManifestId(manifestId)) await ForgetManifestAsync(manifestId);

        // The game's and the launcher's keys go only when they still describe this installation; the launcher's
        // InstallDir goes with the last Ubisoft game, and only while it points at Crimson's runtime.
        var client = _registry.ReadInstalls().GetValueOrDefault(productId);
        var ours = client is null || SamePath(client.InstallDir, root);
        var request = PruneRequest(UbisoftSystemSetupRequests.ForRemoval(productId, manifest, includeGameKeys: ours,
            includeLauncherRecord: ours, lastGame));
        if (!request.IsEmpty)
        {
            EnterFinalizing(job);
            var result = await _setup.ApplyAsync(request, CancellationToken.None);
            if (!result.Applied)
                ReportSecondaryFailure(item, new OperationFailure(OperationFailureKind.Permission, "ubisoft_registry_cleanup_pending",
                    UiStrings.UbisoftRegistryCleanupNotApplied, UiStrings.UbisoftRegistryEntriesAreReplacedOnReinstall, false));
        }
        _log.Information("Ubisoft: uninstalled {Game}", item.AppName);
    }

    private async Task ImportAsync(Job job, uint productId)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        await _store.LoadAsync(ct);
        if (_store.Find(productId) is not null) throw new UbisoftInstallUnavailableException("already_installed");
        if (string.IsNullOrWhiteSpace(item.Location) || !Path.IsPathFullyQualified(item.Location) ||
            !Directory.Exists(item.Location))
            throw new UbisoftInstallUnavailableException("folder_missing");
        var root = UbisoftInstalledStore.NormalizePath(item.Location);
        item.Location = root;
        item.Phase = OperationPhase.Importing;
        item.Status = ActionStatus.Processing;
        Publish(item);

        var product = await RequireInstallableAsync(productId, ct);
        var manifestId = product.LatestManifest!;
        await using var source = new UbisoftReconnectingSource(_connect, productId);
        var manifest = await LoadManifestAsync(source, manifestId, ct);
        var owned = await OwnedForAsync(productId, product, ct);
        var clientLanguage = ClientLanguage(productId, root);
        var languages = ResolveLanguages(item.Languages, clientLanguage,
            UbisoftInstallSelection.AvailableLanguages(manifest, owned));
        var files = UbisoftInstallSelection.Select(manifest, languages, owned);
        if (!ContainsAnyFile(root, files)) throw new UbisoftInstallUnavailableException("import_not_found");
        var matches = files.All(file => Present(root, file));

        // An installation the official client made has its prerequisites in place already.
        var fromClient = clientLanguage is not null;
        var record = new UbisoftInstalledGame
        {
            ProductId = productId,
            InstallPath = root,
            // Keep the build the selection came from even when files differ, so uninstall/repair apply the same
            // manifest instead of a newer build's chunk IDs.
            ManifestId = manifestId,
            NeedsRepair = !matches,
            GameVersion = manifest.GameVersion,
            Languages = languages,
            ChunkIds = ChunkIds(manifest, languages, owned),
            CompletedInstallRuns = fromClient ? manifest.InstallRuns.Select(UbisoftSystemSetupRequests.RunKey).Distinct().ToList() : [],
            SystemSetupPending = true,
            InstallBytes = UbisoftInstallFiles.Bytes(files),
            InstalledAt = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(record, CancellationToken.None);
        EnterFinalizing(job);
        record = await ApplySetupAsync(item, record, manifest, includeRuns: true, CancellationToken.None);
        await _store.SaveAsync(record, CancellationToken.None);
        if (!matches)
        {
            _log.Information("Ubisoft: imported {Game}; its files differ from the manifest, verifying", item.AppName);
            job.FollowUp = new InstallItem(item.AppName, ActionType.Verify, root);
        }
        else _log.Information("Ubisoft: imported {Game}", item.AppName);
    }

    private async Task MoveAsync(Job job, uint productId)
    {
        var ct = job.Cancellation.Token;
        var item = job.Item;
        var installed = await RequireInstalledAsync(productId, ct);
        var source = installed.InstallPath;
        if (string.IsNullOrWhiteSpace(item.MoveLocation) || !Path.IsPathFullyQualified(item.MoveLocation))
            throw new UbisoftInstallUnavailableException("folder_missing");
        var destination = UbisoftInstalledStore.NormalizePath(item.MoveLocation);
        item.Location = source;
        if (SamePath(source, destination)) return;
        PrepareMoveDestination(source, destination);
        item.Phase = OperationPhase.Moving;
        item.Status = ActionStatus.Processing;
        Publish(item);
        InstallPathPolicy.RequireSafeRoot(source);
        InstallPathPolicy.RequireSafeRoot(destination);

        var sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination),
            StringComparison.OrdinalIgnoreCase);
        if (sameVolume) Directory.Move(source, destination);
        else await CopyAndVerifyAsync(job, installed, source, destination, ct);

        EnterFinalizing(job);
        var record = installed with { InstallPath = destination, SystemSetupPending = true };
        await CommitMoveAsync(record, source, destination, sameVolume);
        if (!sameVolume) DeleteMovedSource(source);
        UbisoftManifest? manifest = null;
        try { manifest = await ManifestForRemovalAsync(installed, CancellationToken.None); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.Information("Ubisoft: manifest for {Game} is unavailable after moving ({Error})", item.AppName,
                error.GetType().Name);
        }
        record = await ApplySetupAsync(item, record with { SystemSetupPending = installed.SystemSetupPending }, manifest,
            includeRuns: installed.SystemSetupPending, CancellationToken.None);
        await _store.SaveAsync(record, CancellationToken.None);
        _log.Information("Ubisoft: moved {Game}", item.AppName);
    }

    private async Task CommitMoveAsync(UbisoftInstalledGame record, string source, string destination, bool sameVolume)
    {
        try
        {
            await _store.SaveAsync(record, CancellationToken.None);
        }
        catch
        {
            RollbackMove(source, destination, sameVolume);
            throw;
        }
    }

    private void RollbackMove(string source, string destination, bool sameVolume)
    {
        // The record is the authority for where the installation lives. Put the files back before reporting
        // failure, or a same-volume move would leave installed.json pointing at a folder that no longer exists.
        try
        {
            if (sameVolume)
            {
                if (Directory.Exists(destination) && !Directory.Exists(source))
                    Directory.Move(destination, source);
            }
            else if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Ubisoft: move rollback failed ({Error})", error.GetType().Name);
        }
    }

    private static void PrepareMoveDestination(string source, string destination)
    {
        if (IsInside(destination, source) || IsInside(source, destination))
            throw new UbisoftInstallUnavailableException("move_destination_not_empty");
        if (!Directory.Exists(source)) throw new UbisoftInstallUnavailableException("folder_missing");
        if (Directory.Exists(destination))
        {
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new UbisoftInstallUnavailableException("move_destination_not_empty");
            Directory.Delete(destination);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    }

    /// <summary>Cross-volume move: copy everything, check it, then delete only what was copied from the source.</summary>
    private async Task CopyAndVerifyAsync(Job job, UbisoftInstalledGame installed, string source, string destination,
        CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var files = Directory.EnumerateFiles(source, "*", options).ToList();
        var total = files.Sum(file => new FileInfo(file).Length);
        job.Item.TotalWriteSizeBytes = total;
        job.Item.TotalWriteSizeMb = MiB(total);
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(destination);
            var buffer = new byte[1024 * 1024];
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file);
                var target = InstallPathPolicy.ResolveFile(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                copied.Add(target);
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        Interlocked.Add(ref job.Written, read);
                        Report(job, force: false);
                    }
                }
                File.SetAttributes(target, File.GetAttributes(file));
                if (new FileInfo(target).Length != new FileInfo(file).Length)
                    throw new IOException("A copied file has the wrong size.");
            }

            if (await ManifestForRemovalAsync(installed, ct) is { } manifest)
            {
                var owned = await OwnedOrEmptyAsync(installed.ProductId, ct);
                var selection = InstalledSelection(manifest, installed, owned);
                var damaged = await UbisoftFileVerifier.FindDamagedAsync(destination,
                    selection.Where(file => Present(source, file)).ToList(), _ => { }, job.Pause, ct);
                if (damaged.Count > 0) throw new UbisoftVerificationFailedException(damaged.Count);
            }
        }
        catch
        {
            foreach (var file in copied)
            {
                try { if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            try { InstallPathPolicy.RemoveEmptyDirectories(destination, []); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            throw;
        }
        InstallPathPolicy.RemoveEmptyDirectories(destination, []);
    }

    /// <summary>Only called after the new location is durably recorded.</summary>
    private void DeleteMovedSource(string source)
    {
        try
        {
            InstallPathPolicy.RequireSafeRoot(source);
            foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _log.Warning("Ubisoft: a moved file could not be removed from the old folder ({Error})",
                        error.GetType().Name);
                }
            }
            InstallPathPolicy.RemoveEmptyDirectories(source, []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Warning("Ubisoft: the old move folder could not be cleaned ({Error})", error.GetType().Name);
        }
    }

    /// <summary>
    /// A build can turn a file into a directory (or the reverse) at the same path. The old entry must go before the
    /// transfer, because <see cref="UbisoftInstallFiles.DeleteLeaving"/> deliberately keeps paths present in both.
    /// </summary>
    private void RemoveTypeTransitions(string root, IReadOnlyList<UbisoftManifestFile> previous,
        IReadOnlyList<UbisoftManifestFile> next)
    {
        var old = previous.GroupBy(file => UbisoftInstallFiles.Key(file.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in next)
        {
            var key = UbisoftInstallFiles.Key(file.Path);
            if (!old.TryGetValue(key, out var before) || before.IsDirectory == file.IsDirectory) continue;
            var path = InstallPathPolicy.ResolveFile(root, file.Path);
            if (!before.IsDirectory)
            {
                InstallPathPolicy.DeleteFile(root, path);
                continue;
            }
            var prefix = key + "\\";
            var children = previous.Where(child =>
                UbisoftInstallFiles.Key(child.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            UbisoftInstallFiles.Delete(root, children, _log);
            InstallPathPolicy.RemoveEmptyDirectories(root, []);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new UbisoftInstallUnavailableException("folder_not_empty");
        }
    }

    private async Task TransferAsync(Job job, IUbisoftContentSource source, UbisoftManifest manifest, string manifestId,
        string root, IReadOnlyList<UbisoftManifestFile> files, bool fresh, CancellationToken ct)
    {
        var item = job.Item;
        var productId = ProductId(item.AppName);
        var progress = UbisoftInstallProgress.Load(root, productId, manifestId) ?? new UbisoftInstallProgress
        {
            ProductId = productId,
            ManifestId = manifestId,
            Fresh = fresh
        };
        long written = 0, downloaded = 0;
        foreach (var file in files)
        {
            if (progress.IsComplete(file.Path))
            {
                if (Present(root, file))
                {
                    written += (long)file.Size;
                    downloaded += file.Slices.Sum(slice => (long)slice.DownloadSize);
                    continue;
                }
                progress.Forget(file.Path);
            }
            for (var index = 0; index < file.Slices.Count; index++)
            {
                if (!progress.HasSlice(file.Path, index)) continue;
                written += file.Slices[index].Size;
                downloaded += file.Slices[index].DownloadSize;
            }
        }

        var totalWrite = (long)UbisoftInstallFiles.Bytes(files);
        var totalDownload = (long)UbisoftInstallFiles.DownloadBytes(files);
        item.TotalWriteSizeBytes = totalWrite;
        item.TotalWriteSizeMb = MiB(totalWrite);
        item.TotalDownloadSizeBytes = totalDownload;
        item.TotalDownloadSizeMiB = MiB(totalDownload);
        Interlocked.Exchange(ref job.Written, written);
        Interlocked.Exchange(ref job.Downloaded, downloaded);
        EnterGatedPhase(job, OperationPhase.Transferring);
        StartSampling(job);
        Report(job, force: true);

        var engine = new UbisoftDownloadEngine(source, _cdn, manifest.Compression, manifest.IsCompressed, job.Pause, _log)
        {
            RetryDelay = RetryDelay
        };
        var callbacks = new UbisoftTransferCallbacks(
            bytes =>
            {
                item.TransferRates.RecordDownload(bytes);
                Interlocked.Add(ref job.Downloaded, bytes);
                Report(job, force: false);
            },
            bytes =>
            {
                item.TransferRates.RecordWrite(bytes);
                Interlocked.Add(ref job.Written, bytes);
                Report(job, force: false);
            },
            retrying =>
            {
                if (retrying) Interlocked.Increment(ref item.RetryingDownloadCount);
                else Interlocked.Decrement(ref item.RetryingDownloadCount);
                Report(job, force: true);
            });
        try
        {
            await engine.RunAsync(root, files, progress, callbacks, ct);
        }
        finally
        {
            StopSampling(job);
        }
        Report(job, force: true);
    }

    private async Task<IReadOnlyList<UbisoftManifestFile>> VerifyAsync(Job job, string root,
        IReadOnlyList<UbisoftManifestFile> files, CancellationToken ct)
    {
        var item = job.Item;
        if (!Directory.Exists(root)) throw new UbisoftInstallUnavailableException("folder_missing");
        var total = (long)UbisoftInstallFiles.Bytes(files);
        EnterGatedPhase(job, OperationPhase.Verifying);
        item.TotalWriteSizeBytes = total;
        item.TotalWriteSizeMb = MiB(total);
        Interlocked.Exchange(ref job.Written, 0);
        Report(job, force: true);
        var damaged = await UbisoftFileVerifier.FindDamagedAsync(root, files, bytes =>
        {
            item.TransferRates.RecordWrite(bytes);
            Interlocked.Add(ref job.Written, bytes);
            Report(job, force: false);
        }, job.Pause, ct);
        Report(job, force: true);
        if (damaged.Count > 0)
            _log.Information("Ubisoft: {Count} of {Total} files of {Game} need replacing", damaged.Count, files.Count, item.AppName);
        return damaged;
    }

    /// <summary>A cancelled first install removes the files it created, its resume state, and folders left empty.</summary>
    private async Task CleanupCancelledAsync(InstallItem item)
    {
        if (item.Action != ActionType.Install || string.IsNullOrWhiteSpace(item.Location) ||
            !Path.IsPathFullyQualified(item.Location) || !TryProductId(item.AppName, out var productId)) return;
        await Task.Yield();
        try { await RemoveCancelledInstallAsync(UbisoftInstalledStore.NormalizePath(item.Location), productId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _log.Warning("Ubisoft: files of a cancelled install could not all be removed ({Error})", error.GetType().Name);
        }
    }

    private async Task RemoveCancelledInstallAsync(string root, uint productId)
    {
        if (!Directory.Exists(root) || _store.Find(productId) is not null) return;
        InstallPathPolicy.RequireSafeRoot(root);
        if (await FreshProgressAsync(root, productId) is not { } stored) return;
        // Fresh means the folder was empty when the job began, but the user (or another process) may have created
        // files since. Delete only paths the transfer actually opened; never every manifest chunk.
        DeleteTouchedEntries(root, stored);
        UbisoftInstallProgress.Delete(root);
        RemoveEmptyCrimsonFolder(root);
        InstallPathPolicy.RemoveEmptyDirectories(root, []);
    }

    private void DeleteTouchedEntries(string root, UbisoftInstallProgress progress)
    {
        var paths = new HashSet<string>(progress.Touched, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(progress.Completed);
        paths.UnionWith(progress.Partial.Keys);
        foreach (var relative in paths)
        {
            try { DeleteTouchedEntry(root, relative); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _log.Warning("Ubisoft: a file of a cancelled install could not be removed ({Error})", error.GetType().Name);
            }
        }
    }

    private static void DeleteTouchedEntry(string root, string relative)
    {
        var path = InstallPathPolicy.ResolveFile(root, relative);
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
            return;
        }
        if (File.Exists(path)) InstallPathPolicy.DeleteFile(root, path);
    }

    private static void RemoveEmptyCrimsonFolder(string root)
    {
        var crimson = Path.Combine(root, UbisoftInstallProgress.CrimsonFolder);
        if (Directory.Exists(crimson) && !Directory.EnumerateFileSystemEntries(crimson).Any()) Directory.Delete(crimson);
    }

    private static async Task<UbisoftInstallProgress?> FreshProgressAsync(string root, uint productId)
    {
        var progressFile = UbisoftInstallProgress.PathFor(root);
        if (!File.Exists(progressFile)) return null;
        var stored = JsonSerializer.Deserialize<UbisoftInstallProgress>(await File.ReadAllTextAsync(progressFile));
        if (stored is null || stored.ProductId != productId || !stored.Fresh) return null;
        stored.Completed ??= [];
        stored.Touched ??= [];
        stored.Partial ??= [];
        return stored;
    }

    /// <summary>
    /// A declined prompt leaves the game installed with <see cref="UbisoftInstalledGame.SystemSetupPending"/>.
    /// <paramref name="launcherDirectory"/> is only passed when launching.
    /// </summary>
    private async Task<UbisoftInstalledGame> ApplySetupAsync(InstallItem? item, UbisoftInstalledGame record,
        UbisoftManifest? manifest, bool includeRuns, CancellationToken ct, string? launcherDirectory = null)
    {
        var (built, runKeys) = UbisoftSystemSetupRequests.ForInstall(record.ProductId, record.InstallPath, manifest,
            record.Languages, record.CompletedInstallRuns, includeRuns);
        var request = PruneRequest(built with { LauncherInstallDir = launcherDirectory });
        if (request.IsEmpty) return record with { SystemSetupPending = false };

        var result = await RunSetupAsync(request, ct);
        if (!result.Applied)
        {
            if (item is not null)
                ReportSecondaryFailure(item, new OperationFailure(OperationFailureKind.Permission, "ubisoft_system_setup_pending",
                    UiStrings.UbisoftSystemSetupNotApplied, UiStrings.UbisoftApproveTheAdministratorPrompt, true));
            return record with { SystemSetupPending = true };
        }

        var completed = CompletedRuns(record, built, runKeys, request, result, out var failed);
        if (failed > 0 && item is not null)
            ReportSecondaryFailure(item, new OperationFailure(OperationFailureKind.Unknown, "ubisoft_prerequisite_failed",
                UiStrings.UbisoftPrerequisiteFailed, UiStrings.UbisoftRepairToRetryPrerequisites, true));
        return record with { CompletedInstallRuns = completed, SystemSetupPending = failed > 0 };
    }

    private async Task<UbisoftSystemSetupResult> RunSetupAsync(UbisoftSystemSetupRequest request, CancellationToken ct)
    {
        try { return await _setup.ApplyAsync(request, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.Warning("Ubisoft: system setup could not run ({Error})", error.GetType().Name);
            return new UbisoftSystemSetupResult(false, [], error.GetType().Name);
        }
    }

    private List<string> CompletedRuns(UbisoftInstalledGame record, UbisoftSystemSetupRequest built,
        IReadOnlyList<string> runKeys, UbisoftSystemSetupRequest request, UbisoftSystemSetupResult result, out int failed)
    {
        var completed = record.CompletedInstallRuns.ToList();
        failed = 0;
        for (var index = 0; index < request.Runs.Count; index++)
        {
            var run = request.Runs[index];
            var outcome = index < result.Runs.Count ? result.Runs[index] : null;
            if (outcome is not null && UbisoftSystemSetupRequests.Succeeded(run, outcome.ExitCode))
            {
                var key = runKeys[built.Runs.ToList().IndexOf(run)];
                if (!completed.Contains(key, StringComparer.OrdinalIgnoreCase)) completed.Add(key);
            }
            else
            {
                failed++;
                _log.Warning("Ubisoft: prerequisite {Description} exited with {Code}", run.Description, outcome?.ExitCode);
            }
        }
        return completed;
    }

    /// <summary>
    /// The installation's own record stays while programs run: the helper only runs programs inside a folder the
    /// request registers.
    /// </summary>
    private UbisoftSystemSetupRequest PruneRequest(UbisoftSystemSetupRequest request)
    {
        var writes = request.Writes.Where(write => !_registry.HasValue(write) ||
            request.Runs.Count > 0 && UbisoftSystemSetupPolicy.InstallRoot(write) is not null).ToList();
        var deletes = request.DeleteKeys.Where(_registry.Exists).ToList();
        var launcher = request.LauncherInstallDir is { } directory && _registry.LauncherNeedsDirectory(directory)
            ? directory : null;
        return request with
        {
            Writes = writes, DeleteKeys = deletes, LauncherInstallDir = launcher,
            RemoveLauncherInstallDir = request.RemoveLauncherInstallDir && _registry.LauncherHasCrimsonDirectory()
        };
    }

    private string ManifestCachePath(string manifestId, string extension) =>
        Path.Combine(_providerRoot, "manifests", manifestId.ToUpperInvariant() + extension);

    private UbisoftManifest? CachedManifest(string manifestId)
    {
        if (!IsManifestId(manifestId)) return null;
        var file = ManifestCachePath(manifestId, ".manifest");
        if (!File.Exists(file)) return null;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!string.Equals(UbisoftManifest.ComputeId(bytes), manifestId, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning("Ubisoft: cached manifest {ManifestId} failed its identity check; discarding it", manifestId);
                File.Delete(file);
                return null;
            }
            return UbisoftManifest.Parse(bytes);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            try { File.Delete(file); } catch (IOException) { }
            return null;
        }
    }

    private async Task<UbisoftManifest> LoadManifestAsync(IUbisoftContentSource source, string manifestId, CancellationToken ct)
    {
        if (!IsManifestId(manifestId)) throw new UbisoftInstallUnavailableException("no_manifest");
        if (CachedManifest(manifestId) is { } cached) return cached;
        var bytes = await FetchSignedAsync(source, $"manifests/{manifestId}.manifest", MaximumManifestBytes, ct);
        // The published ID is the SHA-1 of the compressed body. This also authenticates a manifest fetched from a
        // cache or mirror that is not the ownership service.
        if (!string.Equals(UbisoftManifest.ComputeId(bytes), manifestId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded Ubisoft manifest does not match its published ID.");
        var manifest = UbisoftManifest.Parse(bytes);
        WriteCache(ManifestCachePath(manifestId, ".manifest"), bytes);
        return manifest;
    }

    private async Task<UbisoftManifestMetadata> LoadMetadataAsync(IUbisoftContentSource source, string manifestId,
        CancellationToken ct)
    {
        if (!IsManifestId(manifestId)) throw new UbisoftInstallUnavailableException("no_manifest");
        var file = ManifestCachePath(manifestId, ".metadata");
        if (File.Exists(file))
        {
            try { return UbisoftManifestMetadata.Parse(await File.ReadAllBytesAsync(file, ct)); }
            catch (InvalidDataException) { File.Delete(file); }
        }
        var bytes = await FetchSignedAsync(source, $"manifests/{manifestId}.metadata", MaximumMetadataBytes, ct);
        var metadata = UbisoftManifestMetadata.Parse(bytes);
        WriteCache(file, bytes);
        return metadata;
    }

    private async Task<byte[]> FetchSignedAsync(IUbisoftContentSource source, string path, long maximumBytes,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var urls = (await source.GetUrlsAsync([path], ct))[0];
            Exception? last = null;
            foreach (var url in urls)
            {
                try { return await _cdn.GetAsync(url, maximumBytes, null, ct); }
                catch (HttpRequestException error) when (!ct.IsCancellationRequested) { last = error; }
            }
            if (attempt >= 3) throw last ?? new UbisoftInstallUnavailableException("missing_url", OperationFailureKind.Network);
            await Task.Delay(RetryDelay * attempt, ct);
        }
    }

    private void WriteCache(string file, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Ubisoft: manifest could not be cached ({Error})", error.GetType().Name);
        }
    }

    private async Task ForgetManifestAsync(string manifestId)
    {
        if (!IsManifestId(manifestId)) return;
        var stillUsed = (await _store.LoadAsync())
            .Any(game => string.Equals(game.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase));
        if (stillUsed) return;
        try
        {
            var file = ManifestCachePath(manifestId, ".manifest");
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The build an installation's files belong to: its own, or for an unverified import the latest.</summary>
    private async Task<UbisoftManifest?> ManifestForRemovalAsync(UbisoftInstalledGame installed, CancellationToken ct)
    {
        var manifestId = installed.ManifestId;
        if (manifestId is null || !IsManifestId(manifestId))
            manifestId = (await TryGetProductAsync(installed.ProductId, ct))?.LatestManifest;
        if (manifestId is null || !IsManifestId(manifestId)) return null;
        if (CachedManifest(manifestId) is { } cached) return cached;
        try
        {
            await using var source = new UbisoftReconnectingSource(_connect, installed.ProductId);
            return await LoadManifestAsync(source, manifestId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Information("Ubisoft: manifest {Manifest} is unavailable ({Error})", manifestId, error.GetType().Name);
            return null;
        }
    }

    private async Task<(UbisoftManifestMetadata Metadata, IReadOnlySet<uint> Owned, IReadOnlyList<string> Available,
        UbisoftInstalledGame? Installed, UbisoftProductConfig? Config)?> LanguageContextAsync(uint productId, CancellationToken ct)
    {
        await _store.LoadAsync(ct);
        var installed = _store.Find(productId);
        var product = await _products.GetProduct(productId, ct);
        var manifestId = installed?.ManifestId is { } current && IsManifestId(current) ? current : product?.LatestManifest;
        if (manifestId is null || !IsManifestId(manifestId)) return null;
        await using var source = new UbisoftReconnectingSource(_connect, productId);
        var metadata = await LoadMetadataAsync(source, manifestId, ct);
        var owned = await OwnedForAsync(productId, product, ct);
        return (metadata, owned, UbisoftInstallSelection.AvailableLanguages(metadata, owned), installed, ParseConfig(product));
    }

    private async Task<UbisoftOwnedProduct> RequireInstallableAsync(uint productId, CancellationToken ct)
    {
        var product = await _products.GetProduct(productId, ct) ?? throw new UbisoftInstallUnavailableException("not_owned");
        if (!product.Owned) throw new UbisoftInstallUnavailableException("no_download_rights");
        if (!string.IsNullOrWhiteSpace(ParseConfig(product)?.ThirdPartyPlatform))
            throw new UbisoftInstallUnavailableException("third_party");
        if (product.State is not (UbisoftOwnedProduct.StateReleased or UbisoftOwnedProduct.StatePreReleased
            or UbisoftOwnedProduct.StateDownloadable)) throw new UbisoftInstallUnavailableException("not_available");
        if (product.LatestManifest is not { } manifest || !IsManifestId(manifest))
            throw new UbisoftInstallUnavailableException("no_manifest");
        return product;
    }

    private async Task<UbisoftInstalledGame> RequireInstalledAsync(uint productId, CancellationToken ct)
    {
        await _store.LoadAsync(ct);
        return _store.Find(productId) ?? throw new UbisoftInstallUnavailableException("not_installed");
    }

    private async Task<UbisoftOwnedProduct?> TryGetProductAsync(uint productId, CancellationToken ct)
    {
        try { return await _products.GetProduct(productId, ct); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// Uplay ids that gate manifest chunks: every product the account owns (any type: region packages gate WD2's
    /// language chunks), plus this product's own ids and its associations.
    /// </summary>
    private async Task<IReadOnlySet<uint>> OwnedForAsync(uint productId, UbisoftOwnedProduct? product, CancellationToken ct)
    {
        var owned = new HashSet<uint>(await _products.GetOwnedIds(ct)) { productId };
        if (product is null) return owned;
        if (product.UplayId != 0) owned.Add(product.UplayId);
        owned.UnionWith(product.Associations);
        return owned;
    }

    private async Task<IReadOnlySet<uint>> OwnedOrEmptyAsync(uint productId, CancellationToken ct)
    {
        try { return await OwnedForAsync(productId, await TryGetProductAsync(productId, ct), ct); }
        catch (Exception error) when (error is not OperationCanceledException) { return new HashSet<uint> { productId }; }
    }

    private static UbisoftProductConfig? ParseConfig(UbisoftOwnedProduct? product)
    {
        if (product?.Configuration is not { Length: > 0 } yaml) return null;
        try { return UbisoftProductConfig.Parse(yaml, CultureInfo.CurrentUICulture.Name); }
        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The files an installation owns in <paramref name="manifest"/>: its recorded chunks when known, otherwise the
    /// selection for its languages.
    /// </summary>
    private static IReadOnlyList<UbisoftManifestFile> InstalledSelection(UbisoftManifest manifest, UbisoftInstalledGame installed,
        IReadOnlySet<uint> owned)
    {
        if (installed.ChunkIds.Count == 0) return UbisoftInstallSelection.Select(manifest, installed.Languages, owned);
        var chunks = installed.ChunkIds.ToHashSet();
        var files = new Dictionary<string, UbisoftManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in manifest.Chunks.Where(chunk => chunks.Contains(chunk.Id)))
            foreach (var file in chunk.Files)
                files.TryAdd(UbisoftInstallFiles.Key(file.Path), file);
        return files.Values.ToList();
    }

    private static IReadOnlyList<uint> ChunkIds(UbisoftManifest manifest, IReadOnlyCollection<string> languages,
        IReadOnlySet<uint> owned) =>
        manifest.Chunks.Where(chunk => UbisoftInstallSelection.Passes(chunk.UplayIds, chunk.Language, languages, owned))
            .Select(chunk => chunk.Id).Distinct().ToList();

    /// <summary>
    /// Requested languages that the build offers; otherwise the current ones; otherwise the UI default. The game's
    /// own language (registry <c>Language</c> values, launch) is the first entry, so the one closest to the app
    /// language is moved to the front.
    /// </summary>
    private static IReadOnlyList<string> ResolveLanguages(IReadOnlyList<string>? requested, IReadOnlyList<string>? current,
        IReadOnlyList<string> available)
    {
        foreach (var candidate in new[] { requested, current })
        {
            var picked = candidate?.Where(code => available.Contains(code, StringComparer.OrdinalIgnoreCase))
                .Select(code => available.First(known => string.Equals(known, code, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (picked is not { Count: > 0 }) continue;
            var primary = UbisoftInstallSelection.DefaultLanguage(picked, CultureInfo.CurrentUICulture.Name);
            return [primary, .. picked.Where(code => !string.Equals(code, primary, StringComparison.OrdinalIgnoreCase))];
        }
        return available.Count == 0 ? [] : [UbisoftInstallSelection.DefaultLanguage(available, CultureInfo.CurrentUICulture.Name)];
    }

    /// <summary>The official client's language for this product, when its record points at <paramref name="root"/>.</summary>
    private IReadOnlyList<string>? ClientLanguage(uint productId, string root) =>
        _registry.ReadInstalls().GetValueOrDefault(productId) is { } install && SamePath(install.InstallDir, root) &&
        !string.IsNullOrWhiteSpace(install.Language)
            ? [install.Language] : null;

    /// <summary>
    /// A new installation goes into an empty folder, or one holding only Crimson's state for this same product
    /// (an interrupted install resuming). Anything else would mix with, or overwrite, foreign content.
    /// </summary>
    private static void EnsureInstallFolder(string root, uint productId)
    {
        if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
            throw new UbisoftInstallUnavailableException("folder_not_empty");
        if (!Directory.Exists(root)) return;
        var foreign = Directory.EnumerateFileSystemEntries(root).Any(entry =>
            !string.Equals(Path.GetFileName(entry), UbisoftInstallProgress.CrimsonFolder, StringComparison.OrdinalIgnoreCase));
        if (!foreign) return;
        if (UbisoftInstallProgress.BelongsTo(root, productId)) return;
        throw new UbisoftInstallUnavailableException("folder_not_empty");
    }

    private static bool Present(string root, UbisoftManifestFile file)
    {
        try
        {
            var path = InstallPathPolicy.ResolveFile(root, file.Path);
            return file.IsDirectory ? Directory.Exists(path) : File.Exists(path) && new FileInfo(path).Length == (long)file.Size;
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool ContainsAnyFile(string root, IEnumerable<UbisoftManifestFile> files) =>
        files.Any(file => !file.IsDirectory && file.Size > 0 && Present(root, file));

    private static bool IsManifestId(string? id) =>
        id is { Length: 40 } && id.All(char.IsAsciiHexDigit);

    private static bool SamePath(string left, string right)
    {
        static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));
        try { return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            var name = culture.NativeName;
            return name.Length == 0 ? code : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (CultureNotFoundException) { return code; }
    }

    internal static OperationFailure FailureFor(Exception exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => FailureFor(aggregate.InnerExceptions[0]),
        UbisoftVerificationFailedException verification => OperationFailure.Verification(verification.Count),
        UbisoftDownloadDeniedException => new UbisoftInstallUnavailableException("no_download_rights").ToFailure(),
        UbisoftDemuxException => new OperationFailure(OperationFailureKind.Network, "ubisoft_service_unavailable",
            UiStrings.CrimsonCouldNotDownloadTheRequiredData, UiStrings.CheckYourConnectionAndRetry, true),
        NotSupportedException => new UbisoftInstallUnavailableException("unsupported_compression").ToFailure(),
        _ => OperationFailureClassifier.Classify(exception)
    };

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(ActionStatus status) =>
        status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled;

    private static double MiB(long bytes) => bytes / (1024d * 1024d);

    private static string AppName(uint productId) =>
        new GameId(UbisoftLauncherProvider.ProviderId, productId.ToString(CultureInfo.InvariantCulture)).ToString();

    private static bool TryProductId(string appName, out uint productId)
    {
        productId = 0;
        try
        {
            var id = GameId.Parse(appName);
            return id.ProviderId == UbisoftLauncherProvider.ProviderId &&
                uint.TryParse(id.LocalId, NumberStyles.None, CultureInfo.InvariantCulture, out productId) && productId > 0;
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { return false; }
    }

    private static uint ProductId(string appName) => TryProductId(appName, out var productId)
        ? productId : throw new ArgumentException("Expected a Ubisoft product ID.", nameof(appName));

    private void SavePending(InstallItem item)
    {
        try
        {
            Directory.CreateDirectory(_providerRoot);
            var file = Path.Combine(_providerRoot, PendingFile);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new PendingOperation(item.AppName, item.Action,
                item.Location, item.MoveLocation, item.Languages)));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Ubisoft: pending operation could not be saved ({Error})", error.GetType().Name);
        }
    }

    private void DeletePending()
    {
        try
        {
            var file = Path.Combine(_providerRoot, PendingFile);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void StartSampling(Job job)
    {
        StopSampling(job);
        job.Item.TransferRates.SetTransferring(job.Item.Status != ActionStatus.Paused);
        job.Sampler = new Timer(_ => Sample(job), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void StopSampling(Job job)
    {
        job.Sampler?.Dispose();
        job.Sampler = null;
        job.Item.TransferRates.SetTransferring(false);
        job.Item.DownloadSpeedRawMiB = 0;
        job.Item.WriteSpeedMiB = 0;
    }

    private void Sample(Job job)
    {
        try
        {
            if (job.Item.TransferRates.Sample() is not { } sample) return;
            job.Item.DownloadSpeedRawMiB = sample.DownloadMiBPerSecond;
            job.Item.WriteSpeedMiB = sample.WriteMiBPerSecond;
            TransferRatesUpdated?.Invoke(OperationSnapshot.From(job.Item));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Error("Ubisoft: transfer-rate sampling failed ({Error})", error.GetType().Name);
        }
    }

    private void Report(Job job, bool force)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref job.LastReport);
        if (!force && now - last < ProgressInterval.TotalMilliseconds) return;
        if (!force && Interlocked.CompareExchange(ref job.LastReport, now, last) != last) return;
        Interlocked.Exchange(ref job.LastReport, now);
        var item = job.Item;
        var written = Interlocked.Read(ref job.Written);
        var total = item.TotalWriteSizeBytes;
        item.DownloadedSizeMiB = MiB(Interlocked.Read(ref job.Downloaded));
        item.WrittenSizeMiB = MiB(written);
        item.ProgressPercentage = total <= 0 ? 0 : (int)Math.Clamp(written * 100 / total, 0, 100);
        item.RunningTime = DateTime.UtcNow - item.CreatedTime;
        item.Eta = item.DownloadSpeedRawMiB <= 0 || item.Phase != OperationPhase.Transferring ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, total - written) / (item.DownloadSpeedRawMiB * 1024 * 1024));
        InstallProgressUpdate?.Invoke(item);
        OperationUpdated?.Invoke(OperationSnapshot.From(item));
    }

    private void Publish(InstallItem item)
    {
        InstallationStatusChanged?.Invoke(item);
        OperationUpdated?.Invoke(OperationSnapshot.From(item));
    }

    private void Complete(InstallItem item, ActionStatus status, OperationFailure? failure)
    {
        item.Status = status;
        item.Failure = failure;
        if (failure is not null) item.StatusMessage = failure.Message;
        if (status == ActionStatus.Success) item.ProgressPercentage = 100;
        item.DownloadSpeedRawMiB = 0;
        item.WriteSpeedMiB = 0;
        lock (_sync)
        {
            _history.Insert(0, item);
            if (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
        }
        var snapshot = OperationSnapshot.From(item);
        InstallationStatusChanged?.Invoke(item);
        OperationUpdated?.Invoke(snapshot);
        if (failure is not null) OperationError?.Invoke(snapshot with { ShowErrorDialog = true });
    }
}

internal interface IUbisoftClientRegistry
{
    /// <summary><c>HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;id&gt;</c> entries.</summary>
    IReadOnlyDictionary<uint, UbisoftClientInstall> ReadInstalls();

    bool HasValue(UbisoftRegistryWrite write);

    bool Exists(string deletePath);

    /// <summary>
    /// The game's runtime loaders (uplay_r1_loader, upc_r2_loader, 32- and 64-bit) read only the 32-bit view
    /// (KEY_WOW64_32KEY).
    /// </summary>
    bool LauncherNeedsDirectory(string directory);

    /// <summary>True when the launcher InstallDir is a Crimson runtime folder in either view (older builds wrote both).</summary>
    bool LauncherHasCrimsonDirectory();
}

internal sealed record UbisoftClientInstall(string InstallDir, string? Language);

internal sealed class UbisoftWindowsClientRegistry : IUbisoftClientRegistry
{
    private const string InstallsKey = @"SOFTWARE\Ubisoft\Launcher\Installs";
    private const string LauncherKey = @"SOFTWARE\Ubisoft\Launcher";

    public IReadOnlyDictionary<uint, UbisoftClientInstall> ReadInstalls()
    {
        var installs = new Dictionary<uint, UbisoftClientInstall>();
        if (!OperatingSystem.IsWindows()) return installs;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = root.OpenSubKey(InstallsKey);
            if (key is null) return installs;
            foreach (var name in key.GetSubKeyNames())
            {
                if (!uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var productId) || productId == 0)
                    continue;
                using var product = key.OpenSubKey(name);
                if (product?.GetValue("InstallDir") is not string { Length: > 0 } directory) continue;
                installs[productId] = new UbisoftClientInstall(directory, product.GetValue("Language") as string);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return installs;
    }

    public bool HasValue(UbisoftRegistryWrite write)
    {
        if (!OperatingSystem.IsWindows() || !UbisoftSystemSetupPolicy.TrySplit(write.Key, out var subKey, out var name) ||
            name is null) return false;
        foreach (var view in write.BothViews ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32])
            if (!ViewHolds(view, subKey, name, write)) return false;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool ViewHolds(RegistryView view, string subKey, string name, UbisoftRegistryWrite write)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(subKey);
            var value = key?.GetValue(name);
            return write.NumberValue is { } number
                ? value is int stored && unchecked((uint)stored) == number
                : value is string text && string.Equals(text, write.StringValue, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public bool Exists(string deletePath)
    {
        if (!OperatingSystem.IsWindows() || !UbisoftSystemSetupPolicy.TrySplit(deletePath, out var subKey, out var name))
            return false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(subKey);
                if (key is not null && (name is null || key.GetValue(name) is not null)) return true;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return true;
            }
        }
        return false;
    }

    public bool LauncherNeedsDirectory(string directory)
    {
        var wanted = Path.TrimEndingDirectorySeparator(directory);
        return LauncherDirectories(RegistryView.Registry32).Any(current => current is null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(current), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public bool LauncherHasCrimsonDirectory() =>
        LauncherDirectories(RegistryView.Registry64, RegistryView.Registry32)
            .Any(current => current is not null && UbisoftSystemSetupPolicy.IsCrimsonRuntimeDirectory(current));

    private static List<string?> LauncherDirectories(params RegistryView[] views)
    {
        var values = new List<string?>();
        if (!OperatingSystem.IsWindows()) return values;
        foreach (var view in views)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(LauncherKey);
                var current = key?.GetValue("InstallDir") as string;
                values.Add(string.IsNullOrWhiteSpace(current) ? null : current);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return [];
            }
        }
        return values;
    }
}

internal sealed class UbisoftVerificationFailedException(int count) : Exception("Ubisoft files failed verification.")
{
    public int Count { get; } = count;
}

public sealed class UbisoftInstallUnavailableException(string code, OperationFailureKind kind = OperationFailureKind.Manifest)
    : Exception(code)
{
    public string Code { get; } = code;
    public OperationFailureKind Kind { get; } = kind;

    public OperationFailure ToFailure() => Code switch
    {
        "no_manifest" or "not_available" or "unsupported_compression" =>
            Failure(UiStrings.UbisoftNoDownloadableBuild, UiStrings.PleaseTryAgain, false),
        "not_installed" => Failure(UiStrings.UbisoftInstallIsNotRecorded, UiStrings.InstallOrImportTheGameFirst, false),
        "not_owned" or "no_download_rights" =>
            Failure(UiStrings.UbisoftNoLongerOffersThisGameForDownload, UiStrings.PleaseTryAgain, false),
        "third_party" => Failure(UiStrings.UbisoftGameIsSoldThroughAnotherStore, UiStrings.UbisoftInstallItFromThatStore, false),
        "folder_not_empty" or "move_destination_not_empty" or "already_installed" =>
            Failure(UiStrings.UbisoftInstallFolderNotEmpty, UiStrings.UbisoftChooseAnEmptyFolder, false),
        "folder_missing" => Failure(UiStrings.TheInstallationFolderCouldNotBeFound,
            UiStrings.CheckThatTheInstallationFolderIsAvailableAndTryAgain, true),
        "import_not_found" => Failure(UiStrings.NoGameFilesWereFoundInTheSelectedFolder,
            UiStrings.ChooseTheFolderContainingTheGameFilesThenImportAgain, false),
        "missing_url" or "slice_hash_mismatch" =>
            Failure(UiStrings.CrimsonCouldNotDownloadTheRequiredData, UiStrings.CheckYourConnectionAndRetry, true),
        _ => Failure(UiStrings.UbisoftDownloadListIsUnusable, UiStrings.PleaseTryAgain, false)
    };

    private OperationFailure Failure(string message, string suggestion, bool canRetry) =>
        new(Kind, Code, message, suggestion, canRetry);
}
