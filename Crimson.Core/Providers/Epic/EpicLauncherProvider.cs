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
    public ILauncherInstaller Installer { get; }
    public ILauncherStore Store { get; }

    public EpicLauncherProvider(EpicAuthManager auth, EpicLibraryManager library, EpicInstallManager installer, IEpicGamesRepository repository)
    {
        Authentication = new AuthenticationAdapter(auth);
        Library = new LibraryAdapter(library);
        Installer = new InstallerAdapter(installer);
        Store = new StoreAdapter(repository);
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
    }

    private sealed class LibraryAdapter : ILauncherLibrary
    {
        private readonly EpicLibraryManager _library;
        public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
        public event Action<LauncherGame>? GameStatusUpdated;
        public LibraryAdapter(EpicLibraryManager library)
        {
            _library = library;
            library.LibraryUpdated += games => LibraryUpdated?.Invoke(games.Select(ToLauncherGame).ToList());
            library.GameStatusUpdated += game => GameStatusUpdated?.Invoke(ToLauncherGame(game));
        }
        public async Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false) =>
            (await _library.GetLibraryData(forceUpdate)).Select(ToLauncherGame).ToList();
        public LauncherGame? GetGameInfo(string appName) => _library.GetGameInfo(NativeId(appName)) is { } game
            ? ToLauncherGame(game) : null;
        public List<LauncherGame> GetDlcsForGame(string appName) =>
            _library.GetDlcsForGame(NativeId(appName)).Select(ToLauncherGame).ToList();
        public Task<bool> LaunchApp(string appName) => _library.LaunchApp(NativeId(appName));
        public void InvalidateCache() => _library.InvalidateCache();
    }

    private sealed class InstallerAdapter : ILauncherInstaller
    {
        private readonly EpicInstallManager _installer;
        private int _pendingLoaded;
        public bool Supports(ActionType action) => action is ActionType.Install or ActionType.Update
            or ActionType.Repair or ActionType.Uninstall or ActionType.Import or ActionType.Move;
        public bool SupportsPause => true;
        public bool SupportsShortcuts => true;
        private static string Qualified(string nativeId) => new GameId(ProviderId, nativeId).ToString();
        private static InstallItem ToShell(InstallItem item) => item.ForGame(Qualified(item.AppName));
        public InstallItem? CurrentInstall => _installer.CurrentInstall is { } item ? ToShell(item) : null;
        public event Action<InstallItem>? InstallationStatusChanged;
        public event Action<InstallItem>? InstallProgressUpdate;
        public InstallerAdapter(EpicInstallManager installer)
        {
            _installer = installer;
            installer.InstallationStatusChanged += item => InstallationStatusChanged?.Invoke(ToShell(item));
            installer.InstallProgressUpdate += item => InstallProgressUpdate?.Invoke(ToShell(item));
        }
        public void AddToQueue(InstallItem item) => _installer.AddToQueue(item.ForGame(NativeId(item.AppName)));
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

    private sealed class StoreAdapter(IEpicGamesRepository repository) : ILauncherStore
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
    }
}
