using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;

namespace Crimson.Providers.Epic;

/// <summary>Adapts the existing Epic implementation without changing its persisted schema.</summary>
public sealed class EpicLauncherProvider : ILauncherProvider
{
    public const string ProviderId = "epic";
    public string Id => ProviderId;
    public string DisplayName => "Epic Games";
    public ILauncherAuthentication Authentication { get; }
    public ILauncherLibrary Library { get; }
    public ILauncherInstaller? Installer { get; }
    public ILauncherStore Store { get; }

    public EpicLauncherProvider(EpicAuthManager auth, EpicLibraryManager library, EpicInstallManager? installer,
        IEpicGamesRepository repository, CloudSaveManager? cloudSaves = null,
        ILauncherAuthentication? authentication = null)
    {
        Authentication = authentication ?? new AuthenticationAdapter(auth);
        Library = new LibraryAdapter(library, cloudSaves);
        Installer = installer is null ? null : new InstallerAdapter(installer);
        Store = new StoreAdapter(repository, library);
    }

    internal static string NativeId(string appName)
    {
        var id = GameId.Parse(appName);
        if (id.ProviderId != ProviderId)
            throw new ArgumentException("Not an Epic game.", nameof(appName));
        return id.LocalId;
    }

    public static LauncherGame ToLauncherGame(Game game) => new()
    {
        Id = new GameId(ProviderId, game.AppName),
        AppTitle = game.AppTitle,
        CoverImageUrl = game.Metadata?.KeyImages?.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
            ?? game.Metadata?.KeyImages?.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url,
        LandscapeImageUrl = game.Metadata?.KeyImages?.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url,
        IsDownloadableContent = game.Metadata?.MainGameItem is not null,
        LocalAppState = game.LocalAppState is { } state
            ? new LauncherInstallation(state.InstallStatus, state.InstallPath) : null
    };

    private sealed class AuthenticationAdapter : ILauncherAuthentication
    {
        private readonly EpicAuthManager _auth;
        public AuthenticationStatus AuthenticationStatus => _auth.AuthenticationStatus;
        public event Action<AuthenticationStatus>? StatusChanged;
        public AuthenticationAdapter(EpicAuthManager auth)
        {
            _auth = auth;
            auth.AuthStatusChanged += (_, args) => StatusChanged?.Invoke(args.NewStatus);
        }
        public Task<AuthenticationStatus> CheckAuthStatus() => _auth.CheckAuthStatus();
        public Task Logout() => _auth.Logout();
        public async Task<LauncherUser?> GetUser(CancellationToken cancellationToken = default)
        {
            try
            {
                var data = await _auth.GetUserData().WaitAsync(cancellationToken);
                return new LauncherUser(data?.DisplayName, data?.AccountId);
            }
            catch (Exception) { return null; } // Identity is display-only; a failure must not break the row.
        }
    }

    private sealed class LibraryAdapter : ILauncherLibrary, IGameDetailsSource, IGameCloudSaves
    {
        private readonly EpicLibraryManager _library;
        private readonly CloudSaveManager? _cloudSaves;
        public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
        public event Action<LauncherGame>? GameStatusUpdated;
        public event Action<string, bool>? RunningStateChanged;
        public event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;
        public LibraryAdapter(EpicLibraryManager library, CloudSaveManager? cloudSaves = null)
        {
            _library = library;
            _cloudSaves = cloudSaves;
            library.LibraryUpdated += games => LibraryUpdated?.Invoke(games.Select(ToLauncherGame).ToList());
            library.GameStatusUpdated += game => GameStatusUpdated?.Invoke(ToLauncherGame(game));
            library.GameRunningStateChanged += (appName, running) =>
                RunningStateChanged?.Invoke(Qualified(appName), running);
            if (cloudSaves is not null)
                cloudSaves.SyncStatusChanged += (appName, status) =>
                    SyncStatusChanged?.Invoke(Qualified(appName), status);
        }
        public async Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false) =>
            (await _library.GetLibraryData(forceUpdate)).Select(ToLauncherGame).ToList();
        public LauncherGame? GetGameInfo(string appName) => _library.GetGameInfo(NativeId(appName)) is { } game
            ? ToLauncherGame(game) : null;
        public List<LauncherGame> GetDlcsForGame(string appName) =>
            _library.GetDlcsForGame(NativeId(appName)).Select(ToLauncherGame).ToList();
        public Task<bool> LaunchApp(string appName) => _library.LaunchApp(NativeId(appName));
        public void InvalidateCache() => _library.InvalidateCache();
        public string GetBaseGameAppName(string appName) => Qualified(_library.GetBaseGameAppName(NativeId(appName)));

        public bool IsRunning(string appName) => _library.IsGameRunning(NativeId(appName));
        public bool AllowsMultipleInstances(string appName) =>
            _library.GetGameInfo(NativeId(appName)) is { } game && EpicLibraryManager.AllowsMultipleInstances(game);
        public bool Supports(string appName) =>
            _cloudSaves is not null && CloudSaveManager.SupportsCloudSaves(ForGame(appName));
        public CloudSaveSyncStatus GetSyncStatus(string appName) =>
            _cloudSaves?.GetSyncStatus(NativeId(appName)) ?? CloudSaveSyncStatus.Idle;
        public Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) =>
            _library.GetGamePlaytimeSeconds(NativeId(appName), cancellationToken);
        public Uri? GetStorePageUri(string appName) => EpicStoreLinks.ForGame(ForGame(appName));

        public LauncherGameDetails? GetGameDetails(string appName)
        {
            var game = ForGame(appName);
            if (game?.Metadata is null) return null;
            return new LauncherGameDetails(
                CoverImageUrl: EpicContentImages.Find(game.Metadata.KeyImages, "DieselGameBoxTall", "OfferImageTall", "DieselGameBox"),
                LandscapeImageUrl: EpicContentImages.FindLandscape(game.Metadata.KeyImages, "DieselGameBox", "OfferImageWide"),
                DeveloperName: game.Metadata.Developer,
                Description: game.Metadata.Description,
                InstalledVersion: game.LocalAppState?.Version,
                LastUpdatedAt: game.LocalAppState?.LastUpdatedAt);
        }

        public async Task<StoreProductDetails?> GetStoreProductDetails(string appName,
            CancellationToken cancellationToken = default)
        {
            var nameSpace = ForGame(appName)?.Metadata?.Namespace;
            if (string.IsNullOrWhiteSpace(nameSpace)) return null;
            var details = await _library.GetStoreProductDetails(nameSpace, cancellationToken);
            return details is null ? null : EpicContentImages.TrustStoreDetails(details);
        }

        private Game? ForGame(string appName) => _library.GetGameInfo(NativeId(appName));

        private static string Qualified(string nativeId) => new GameId(ProviderId, nativeId).ToString();
    }

    private sealed class InstallerAdapter : ILauncherInstaller
    {
        private readonly EpicInstallManager _installer;
        // Shell items are qualified copies; a queued job must keep one stable Epic item so
        // later notices and failures land on the row the queue actually holds.
        private readonly Dictionary<InstallItem, InstallItem> _nativeItems = new(ReferenceEqualityComparer.Instance);
        private int _pendingLoaded;
        private InstallItem Native(InstallItem item)
        {
            if (string.IsNullOrWhiteSpace(item.AppName)) return item;
            lock (_nativeItems)
            {
                if (_nativeItems.TryGetValue(item, out var native)) return native;
                native = item.ForGame(NativeId(item.AppName));
                _nativeItems[item] = native;
                return native;
            }
        }
        public bool Supports(ActionType action) => action is ActionType.Install or ActionType.Update
            or ActionType.Repair or ActionType.Uninstall or ActionType.Import or ActionType.Move;
        public bool SupportsPause => true;
        public bool SupportsShortcuts => true;
        private static string Qualified(string nativeId) => new GameId(ProviderId, nativeId).ToString();
        private static InstallItem ToShell(InstallItem item) =>
            string.IsNullOrWhiteSpace(item.AppName) ? item : item.ForGame(Qualified(item.AppName));
        private static OperationSnapshot ToShell(OperationSnapshot operation) =>
            string.IsNullOrWhiteSpace(operation.AppName) ? operation
                : operation with { AppName = Qualified(operation.AppName) };
        public InstallItem? CurrentInstall => _installer.CurrentInstall is { } item ? ToShell(item) : null;
        public OperationSnapshot? CurrentOperation => _installer.CurrentOperation is { } operation ? ToShell(operation) : null;
        public IReadOnlyList<OperationSnapshot> GetQueueOperations() =>
            _installer.GetQueueOperations().Select(ToShell).ToList();
        public IReadOnlyList<OperationSnapshot> GetHistoryOperations() =>
            _installer.GetHistoryOperations().Select(ToShell).ToList();
        public bool RetryFailedOperation(string appName) => _installer.RetryFailedOperation(NativeId(appName));
        public OperationSnapshot? GetActiveOperationForGame(string appName) =>
            _installer.GetActiveOperationForGame(NativeId(appName)) is { } operation ? ToShell(operation) : null;
        public bool TryAddToQueue(InstallItem item) => _installer.TryAddToQueue(Native(item));
        public void ReportPreparationFailure(string appName, ActionType action, OperationFailure failure) =>
            _installer.ReportPreparationFailure(NativeId(appName), action, failure);
        public void ReportPreparationFailure(string appName, ActionType action, Exception exception) =>
            _installer.ReportPreparationFailure(NativeId(appName), action, exception);
        public void ReportSecondaryFailure(InstallItem install, OperationFailure failure) =>
            _installer.ReportSecondaryFailure(Native(install), failure);
        public event Action<InstallItem>? InstallationStatusChanged;
        public event Action<InstallItem>? InstallProgressUpdate;
        public event Action<OperationSnapshot>? OperationUpdated;
        public event Action<OperationSnapshot>? TransferRatesUpdated;
        public event Action<OperationSnapshot>? OperationError;
        public InstallerAdapter(EpicInstallManager installer)
        {
            _installer = installer;
            installer.InstallationStatusChanged += item => InstallationStatusChanged?.Invoke(ToShell(item));
            installer.InstallProgressUpdate += item => InstallProgressUpdate?.Invoke(ToShell(item));
            installer.OperationUpdated += operation => OperationUpdated?.Invoke(ToShell(operation));
            installer.TransferRatesUpdated += operation => TransferRatesUpdated?.Invoke(ToShell(operation));
            installer.OperationError += operation => OperationError?.Invoke(ToShell(operation));
        }
        public void AddToQueue(InstallItem item) => _installer.AddToQueue(Native(item));
        public List<string> GetQueueItemNames() => _installer.GetQueueItemNames().Select(Qualified).ToList();
        public List<string> GetHistoryItemsNames() => _installer.GetHistoryItemsNames().Select(Qualified).ToList();
        public void CancelInstall(string appName) => _installer.CancelInstall(NativeId(appName));
        public void PauseInstall() => _installer.PauseInstall();
        public void ResumeInstall() => _installer.ResumeInstall();
        public Task StopProcessing() => _installer.StopProcessing();
        public async Task LoadPendingInstalls()
        {
            if (Interlocked.CompareExchange(ref _pendingLoaded, 1, 0) != 0) return;
            try { await _installer.LoadPendingInstalls(); }
            catch { Interlocked.Exchange(ref _pendingLoaded, 0); throw; }
        }
        public async Task<(double DownloadBytes, double InstallBytes)> GetGameDownloadInstallSizes(
            string appName, CancellationToken cancellationToken = default)
        {
            var sizes = await _installer.GetGameDownloadInstallSizes(NativeId(appName), cancellationToken);
            return (sizes.totalDownloadSizeMb, sizes.totalWriteSizeMb);
        }
    }

    private sealed class StoreAdapter(IEpicGamesRepository repository, EpicLibraryManager library) : ILauncherStore
    {
        public string DisplayName => "Epic Games Store";
        public Uri HomeUri { get; } = new("https://store.epicgames.com/");
        public Uri CreateSearchUri(string query) => new(HomeUri,
            $"en-US/browse?q={Uri.EscapeDataString(query.Trim())}&sortBy=relevancy&sortDir=DESC&count=40");
        public Uri CreateProductUri(string productId) => new(HomeUri,
            $"en-US/p/{Uri.EscapeDataString(productId.Trim('/'))}");
        public async Task<IReadOnlyList<StoreProduct>> SearchStore(string query, CancellationToken cancellationToken = default) =>
            (await repository.SearchStore(query, cancellationToken))
                .Select(result => new StoreProduct(ProviderId, result.Title, result.ProductSlug, result.ImageUrl)).ToList();
        public void ClearCache() => library.ClearStoreProductDetailsCache();
    }
}

/// <summary>Picks provider artwork for the shell, but only from trusted Epic content hosts.</summary>
public static class EpicContentImages
{
    public static string? Find(IEnumerable<KeyImage>? images, params string[] preferredTypes) =>
        FindMatching(images, image => true, preferredTypes);

    /// <summary>Wide artwork only; portrait and square box art would crop badly in a banner.</summary>
    public static string? FindLandscape(IEnumerable<KeyImage>? images, params string[] preferredTypes) =>
        FindMatching(images, image =>
            image.Width <= 0 || image.Height <= 0 || image.Width > image.Height, preferredTypes);

    /// <summary>Drops Store artwork that does not belong to a trusted Epic content host.</summary>
    public static StoreProductDetails TrustStoreDetails(StoreProductDetails details) => new()
    {
        ProductName = details.ProductName,
        DeveloperName = details.DeveloperName,
        PublisherName = details.PublisherName,
        ShortDescription = details.ShortDescription,
        LongDescription = details.LongDescription,
        WebsiteUrl = details.WebsiteUrl,
        ReleaseDate = details.ReleaseDate,
        Images = details.Images.Where(image => EpicEndpointPolicy.TryGetContentUri(image.Url, out _)).ToList(),
        Tags = details.Tags,
        SupportedAudioLanguages = details.SupportedAudioLanguages,
        SupportedTextLanguages = details.SupportedTextLanguages,
        WindowsRequirements = details.WindowsRequirements
    };

    private static string? FindMatching(IEnumerable<KeyImage>? images,
        Func<KeyImage, bool> isUsable, string[] preferredTypes) =>
        preferredTypes.Select(type => images?.FirstOrDefault(image =>
                string.Equals(image.Type, type, StringComparison.OrdinalIgnoreCase) &&
                isUsable(image) &&
                EpicEndpointPolicy.TryGetContentUri(image.Url, out _))?.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
}
