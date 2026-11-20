using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers.Ubisoft.Protocol;
using Crimson.Resources;
using Serilog;

namespace Crimson.Providers.Ubisoft;

public interface IUbisoftRunningGame : IAsyncDisposable { Task Completion { get; } }

public interface IUbisoftRuntime
{
    Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftLaunchPlan plan, CancellationToken token);
}

public sealed class UbisoftLibrary : ILauncherLibrary, IGameDetailsSource, IGameCloudSaves
{
    private readonly UbisoftAuthentication _auth;
    private readonly UbisoftOwnershipCache _ownership;
    private readonly UbisoftMetadata _metadata;
    private readonly UbisoftInstalledStore _installed;
    private readonly IUbisoftRuntime _runtime;
    private readonly UbisoftCloudSaves? _cloudSaves;
    private readonly Func<uint, CancellationToken, Task<bool>>? _prepareLaunch;
    private readonly Func<string, string?>? _readRegistry;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly object _lifetimeSync = new();
    private CancellationTokenSource _runtimeLifetime = new();
    private IReadOnlyList<UbisoftOwnedProduct>? _products;
    private IUbisoftRunningGame? _running;
    private uint? _playing;
    private int _epoch;
    private int _backgroundRefresh;

    public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
    public event Action<LauncherGame>? GameStatusUpdated;
    public event Action<string, bool>? RunningStateChanged;
    public event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;
    public string? LastLaunchError { get; private set; }

    /// <param name="prepareLaunch">Applies pending system setup in at most one UAC prompt; true when nothing is left pending.</param>
    public UbisoftLibrary(UbisoftAuthentication auth, UbisoftOwnershipCache ownership, UbisoftMetadata metadata,
        UbisoftInstalledStore installed, IUbisoftRuntime runtime, ILogger log, UbisoftCloudSaves? cloudSaves = null,
        Func<uint, CancellationToken, Task<bool>>? prepareLaunch = null, Func<string, string?>? readRegistry = null)
    {
        _auth = auth;
        _ownership = ownership;
        _metadata = metadata;
        _installed = installed;
        _runtime = runtime;
        _log = log;
        _cloudSaves = cloudSaves;
        _prepareLaunch = prepareLaunch;
        _readRegistry = readRegistry;
        if (cloudSaves is not null) cloudSaves.SyncStatusChanged += (app, status) => SyncStatusChanged?.Invoke(app, status);
        installed.Changed += OnInstalledChanged;
        // The installer can refresh ownership too (a product bought since the last refresh).
        ownership.Changed += OnOwnershipChanged;
        auth.StatusChanged += OnAuthenticationChanged;
    }

    private void OnInstalledChanged(uint productId)
    {
        if (_products is null) return;
        if (_products.FirstOrDefault(product => product.ProductId == productId && IsVisible(product)) is { } product)
            GameStatusUpdated?.Invoke(ToGame(product, _products));
    }

    private void OnOwnershipChanged()
    {
        if (_products is null || _auth.AuthenticationStatus != AuthenticationStatus.LoggedIn) return;
        _products = _ownership.Products;
        LibraryUpdated?.Invoke(Snapshot());
    }

    private void OnAuthenticationChanged(AuthenticationStatus status)
    {
        if (status is not (AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed)) return;
        Interlocked.Increment(ref _epoch);
        lock (_lifetimeSync) _runtimeLifetime.Cancel();
        _products = null;
        Interlocked.Exchange(ref _backgroundRefresh, 0);
        _ = StopRuntime();
    }

    public async Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false)
    {
        await _installed.LoadAsync();
        if (_products is not null && !forceUpdate) return Snapshot();
        var session = await _auth.RequireSession();
        var epoch = Volatile.Read(ref _epoch);
        if (!forceUpdate)
        {
            // A cold start shows the cached list at once and replaces it when the live list arrives.
            var cached = await _ownership.LoadAsync(session.UserId);
            if (cached.Count > 0 && IsCurrent(session, epoch))
            {
                _products = cached;
                RefreshInBackground(session, epoch);
                return Publish();
            }
        }
        var products = await _ownership.RefreshAsync(session);
        if (!IsCurrent(session, epoch)) return [];
        _products = products;
        return Publish();
    }

    private void RefreshInBackground(UbisoftSession session, int epoch)
    {
        if (_ownership.IsLive || Interlocked.Exchange(ref _backgroundRefresh, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var products = await _ownership.RefreshAsync(session);
                if (!IsCurrent(session, epoch)) return;
                _products = products;
                Publish();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Interlocked.Exchange(ref _backgroundRefresh, 0);
                _log.Warning("Ubisoft: ownership refresh failed, showing the cached library ({ErrorType})", error.GetType().Name);
            }
        });
    }

    // Extension replaces the session object for the same account; only a sign-out or another account invalidates a load.
    private bool IsCurrent(UbisoftSession session, int epoch) =>
        epoch == Volatile.Read(ref _epoch) && _auth.Session?.UserId == session.UserId;

    private List<LauncherGame> Publish()
    {
        var snapshot = Snapshot();
        LibraryUpdated?.Invoke(snapshot);
        return snapshot;
    }

    internal static bool IsVisible(UbisoftOwnedProduct product) =>
        product.Owned && product.ProductType is UbisoftOwnedProduct.TypeGame or UbisoftOwnedProduct.TypeAddon;

    /// <summary>
    /// <c>target_partner</c> only says where the licence came from (Epic Games for giveaway copies such as
    /// Watch Dogs 2); Ubisoft still serves those builds, so it is not checked.
    /// </summary>
    public static bool IsInstallable(UbisoftOwnedProduct product, UbisoftProductConfig? config) =>
        product.Owned &&
        product.State is UbisoftOwnedProduct.StateReleased or UbisoftOwnedProduct.StatePreReleased or UbisoftOwnedProduct.StateDownloadable &&
        !string.IsNullOrWhiteSpace(product.LatestManifest) &&
        string.IsNullOrWhiteSpace(config?.ThirdPartyPlatform);

    private List<LauncherGame> Snapshot()
    {
        var products = _products;
        if (products is null) return [];
        return products.Where(IsVisible).Select(product => ToGame(product, products)).ToList();
    }

    private LauncherGame ToGame(UbisoftOwnedProduct product, IReadOnlyList<UbisoftOwnedProduct> products)
    {
        var installed = _installed.Find(product.ProductId);
        var config = _metadata.Config(product);
        var display = _metadata.Describe(product, _ownership.ParentOf(product, products));
        var isGame = product.ProductType == UbisoftOwnedProduct.TypeGame;
        return new LauncherGame
        {
            Id = Id(product.ProductId),
            AppTitle = display.Title,
            CoverImageUrl = display.CoverImageUrl,
            LandscapeImageUrl = display.LandscapeImageUrl,
            IsDownloadableContent = !isGame,
            CanInstall = IsInstallable(product, config),
            CanLaunch = isGame && installed is not null && !installed.NeedsRepair &&
                product.State != UbisoftOwnedProduct.StateExpired &&
                config is not null && (config.OnlineExecutables.Count > 0 || config.OfflineExecutables.Count > 0),
            LocalAppState = installed is null ? null : new LauncherInstallation(State(product, installed), installed.InstallPath)
        };
    }

    private InstallState State(UbisoftOwnedProduct product, UbisoftInstalledGame installed)
    {
        if (_playing == product.ProductId) return InstallState.Playing;
        if (installed.NeedsRepair) return InstallState.Broken;
        return NeedsUpdate(product, installed) ? InstallState.NeedUpdate : InstallState.Installed;
    }

    internal static bool NeedsUpdate(UbisoftOwnedProduct product, UbisoftInstalledGame installed) =>
        !string.IsNullOrWhiteSpace(installed.ManifestId) && !string.IsNullOrWhiteSpace(product.LatestManifest) &&
        !string.Equals(installed.ManifestId, product.LatestManifest, StringComparison.OrdinalIgnoreCase);

    private static GameId Id(uint productId) =>
        new(UbisoftLauncherProvider.ProviderId, productId.ToString(CultureInfo.InvariantCulture));

    private static uint NativeId(string appName)
    {
        var id = GameId.Parse(appName);
        if (id.ProviderId != UbisoftLauncherProvider.ProviderId ||
            !uint.TryParse(id.LocalId, NumberStyles.None, CultureInfo.InvariantCulture, out var productId))
            throw new ArgumentException("Expected a Ubisoft product ID.");
        return productId;
    }

    private UbisoftOwnedProduct? Find(uint productId) =>
        _products?.FirstOrDefault(product => product.ProductId == productId && IsVisible(product));

    public LauncherGame? GetGameInfo(string appName)
    {
        var productId = NativeId(appName);
        var products = _products;
        return products is not null && Find(productId) is { } product ? ToGame(product, products) : null;
    }

    public List<LauncherGame> GetDlcsForGame(string appName)
    {
        var productId = NativeId(appName);
        var products = _products;
        if (products is null) return [];
        return AddonsOf(productId, products).Select(product => ToGame(product, products)).ToList();
    }

    private IEnumerable<UbisoftOwnedProduct> AddonsOf(uint gameId, IReadOnlyList<UbisoftOwnedProduct> products) =>
        products.Where(product => IsVisible(product) && product.ProductType == UbisoftOwnedProduct.TypeAddon &&
            _ownership.ParentOf(product, products)?.ProductId == gameId);

    public string GetBaseGameAppName(string appName)
    {
        var productId = NativeId(appName);
        var products = _products;
        var parent = products is not null && Find(productId) is { } product ? _ownership.ParentOf(product, products) : null;
        return parent is null ? appName : Id(parent.ProductId).ToString();
    }

    public bool IsRunning(string appName) => _playing == NativeId(appName);

    // One Ubisoft title runs at a time: the runtime's pipe names are per session, not per game.
    public bool AllowsMultipleInstances(string appName) { NativeId(appName); return false; }

    public LauncherGameDetails? GetGameDetails(string appName)
    {
        var products = _products;
        if (products is null || Find(NativeId(appName)) is not { } product) return null;
        var display = _metadata.Describe(product, _ownership.ParentOf(product, products));
        var installed = _installed.Find(product.ProductId);
        return new LauncherGameDetails(display.CoverImageUrl, display.LandscapeImageUrl, Description: display.Description,
            InstalledVersion: installed?.GameVersion, LastUpdatedAt: installed is null ? null : installed.UpdatedAt ?? installed.InstalledAt);
    }

    public Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken token = default)
    {
        NativeId(appName);
        return Task.FromResult<long?>(null); // The ownership data does not carry playtime.
    }

    public Task<StoreProductDetails?> GetStoreProductDetails(string appName, CancellationToken token = default)
    {
        NativeId(appName);
        return Task.FromResult<StoreProductDetails?>(null);
    }

    public Uri? GetStorePageUri(string appName) { NativeId(appName); return null; }

    public bool Supports(string appName) =>
        _cloudSaves is not null && Find(NativeId(appName)) is { } product && _metadata.Config(product)?.CloudSaves == true;

    public CloudSaveSyncStatus GetSyncStatus(string appName) =>
        _cloudSaves?.GetSyncStatus(Id(NativeId(appName)).ToString()) ?? CloudSaveSyncStatus.Idle;

    public void InvalidateCache() { Interlocked.Increment(ref _epoch); _products = null; }

    public async Task<bool> LaunchApp(string appName)
    {
        await _launchGate.WaitAsync();
        try
        {
            LastLaunchError = null;
            if (_running is not null) throw new InvalidOperationException(UiStrings.UbisoftGameAlreadyRunning);
            await LaunchAsync(NativeId(appName));
            return true;
        }
        catch (Exception error)
        {
            LastLaunchError = error is InvalidOperationException or FileNotFoundException
                ? error.Message : $"Ubisoft launch failed ({error.GetType().Name}).";
            _log.Warning("Ubisoft launch failed: {ErrorType}", error.GetType().Name);
            return false;
        }
        finally { _launchGate.Release(); }
    }

    private async Task LaunchAsync(uint productId)
    {
        var session = await _auth.RequireSession();
        var epoch = Volatile.Read(ref _epoch);
        var (product, config, plan) = await ResolveLaunchAsync(productId);
        var products = _products ?? [];
        plan = plan with
        {
            AddonIds = AddonsOf(productId, products).Select(addon => addon.ProductId).ToList(),
            SdkProducts = UbisoftSdkProduct.For(product, products),
            SaveDirectory = UbisoftCloudSaves.SaveDirectory(_ownership.ProviderRoot, session.UserId, productId)
        };
        Directory.CreateDirectory(plan.SaveDirectory);
        var cloud = await SyncCloudSavesBeforeLaunchAsync(session, product, config);

        var running = await StartRuntimeAsync(session, epoch, plan);
        _running = running;
        _playing = productId;
        if (_products is { } current) GameStatusUpdated?.Invoke(ToGame(product, current));
        RunningStateChanged?.Invoke(Id(productId).ToString(), true);
        _ = ObserveGame(running, product, session, cloud);
    }

    private async Task<(UbisoftOwnedProduct Product, UbisoftProductConfig Config, UbisoftLaunchPlan Plan)>
        ResolveLaunchAsync(uint productId)
    {
        if (_products is null) await GetLibraryData();
        var product = Find(productId) ?? throw new InvalidOperationException(UiStrings.UbisoftInstallIsNotRecorded);
        var installed = _installed.Find(productId) ?? throw new InvalidOperationException(UiStrings.UbisoftInstallIsNotRecorded);
        if (installed.NeedsRepair) throw new InvalidOperationException(UiStrings.RepairRequired);
        installed = await PrepareSystemAsync(productId, installed);
        var config = _metadata.Config(product) ?? throw new InvalidOperationException(UiStrings.UbisoftLaunchExecutableMissing);
        var plan = UbisoftLaunchPlan.Resolve(installed, product, config, _readRegistry);
        if (plan.ApplicationId is null) throw new InvalidOperationException(UiStrings.UbisoftApplicationIdMissing);
        PlaceRuntimeFiles(productId, plan);
        return (product, config, plan);
    }

    private async Task<UbisoftInstalledGame> PrepareSystemAsync(uint productId, UbisoftInstalledGame installed)
    {
        if (_prepareLaunch is null) return installed;
        // Ubisoft Connect may have repointed the runtime folder since the last launch. Without the setup the
        // game cannot find the runtime or its own files.
        if (!await _prepareLaunch(productId, CancellationToken.None))
        {
            _log.Warning("Ubisoft: system setup for {Product} is still pending; not launching", productId);
            throw new InvalidOperationException(UiStrings.UbisoftLaunchNeedsAdministrator);
        }
        return _installed.Find(productId) ?? installed;
    }

    private void PlaceRuntimeFiles(uint productId, UbisoftLaunchPlan plan)
    {
        // Games built for the pre-2013 launcher never load this runtime; starting one only shows its own error box.
        var loaders = UbisoftRuntimeProbe.FindLoaders(plan.InstallPath);
        if (loaders.Count == 0) throw new InvalidOperationException(UiStrings.UbisoftLegacyLauncherRequired);
        var copied = UbisoftRuntimeFiles.Provision(plan.InstallPath,
            loaders.Select(Path.GetDirectoryName).Append(Path.GetDirectoryName(plan.Executable)).OfType<string>());
        if (copied.Count > 0) _log.Information("Ubisoft: placed {Count} runtime files in {Product}", copied.Count, productId);
    }

    private async Task<UbisoftCloudSaves?> SyncCloudSavesBeforeLaunchAsync(UbisoftSession session, UbisoftOwnedProduct product,
        UbisoftProductConfig config)
    {
        if (_cloudSaves is null || !config.CloudSaves) return null;
        await _cloudSaves.WaitForIdleAsync(product.ProductId);
        await _cloudSaves.SyncBeforeLaunchAsync(session, product);
        return _cloudSaves;
    }

    private async Task<IUbisoftRunningGame> StartRuntimeAsync(UbisoftSession session, int epoch, UbisoftLaunchPlan plan)
    {
        CancellationToken lifetime;
        lock (_lifetimeSync)
        {
            if (_auth.AuthenticationStatus != AuthenticationStatus.LoggedIn || !IsCurrent(session, epoch))
                throw new InvalidOperationException(UiStrings.UbisoftReconnectAccount);
            _runtimeLifetime.Dispose();
            _runtimeLifetime = new CancellationTokenSource();
            lifetime = _runtimeLifetime.Token;
        }
        var running = await _runtime.Start(session, plan, lifetime);
        if (lifetime.IsCancellationRequested) { await running.DisposeAsync(); lifetime.ThrowIfCancellationRequested(); }
        return running;
    }

    private async Task ObserveGame(IUbisoftRunningGame running, UbisoftOwnedProduct product, UbisoftSession session,
        UbisoftCloudSaves? cloud)
    {
        try { await running.Completion; }
        catch (Exception error)
        {
            LastLaunchError = error is InvalidOperationException ? error.Message : $"Ubisoft runtime failed ({error.GetType().Name}).";
            _log.Warning("Ubisoft runtime ended: {ErrorType}", error.GetType().Name);
        }
        finally
        {
            await running.DisposeAsync();
            await _launchGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_running, running))
                {
                    _running = null;
                    _playing = null;
                    if (_products is { } products) GameStatusUpdated?.Invoke(ToGame(product, products));
                    RunningStateChanged?.Invoke(Id(product.ProductId).ToString(), false);
                }
            }
            finally { _launchGate.Release(); }
        }
        // The session may have been extended while playing; never upload under another account.
        var current = _auth.Session;
        if (cloud is not null && current is not null && current.UserId == session.UserId)
            await cloud.SyncAfterExitAsync(current, product);
    }

    private async Task StopRuntime()
    {
        await _launchGate.WaitAsync();
        try { if (_running is not null) await _running.DisposeAsync(); }
        finally { _launchGate.Release(); }
    }
}
