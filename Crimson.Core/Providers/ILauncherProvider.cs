using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Providers;

/// <summary>Register one instance per launcher. Each owns its session, caches and work queues.</summary>
public interface ILauncherProvider
{
    string Id { get; }
    string DisplayName { get; }
    ILauncherAuthentication Authentication { get; }
    ILauncherLibrary Library { get; }
    ILauncherInstaller? Installer { get; }
    ILauncherStore? Store { get; }
}

// Sign-in UI belongs to the frontend adapter: browser, password/MFA, device or external flows
// need not mimic Epic's exchange-code protocol. Tokens remain inside the provider.
public interface ILauncherAuthentication
{
    AuthenticationStatus AuthenticationStatus { get; }
    event Action<AuthenticationStatus>? StatusChanged;
    Task<AuthenticationStatus> CheckAuthStatus();
    Task Logout();
}

public interface ILauncherLibrary
{
    event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
    event Action<LauncherGame>? GameStatusUpdated;
    Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false);
    LauncherGame? GetGameInfo(string appName);
    List<LauncherGame> GetDlcsForGame(string appName);
    Task<bool> LaunchApp(string appName);
    void InvalidateCache();
}

/// <summary>Provider-owned installer; does not prescribe manifests, slices or download protocols.</summary>
public interface ILauncherInstaller
{
    bool Supports(ActionType action);
    bool SupportsPause { get; }
    bool SupportsShortcuts { get; }
    InstallItem? CurrentInstall { get; }
    event Action<InstallItem>? InstallationStatusChanged;
    event Action<InstallItem>? InstallProgressUpdate;
    void AddToQueue(InstallItem item);
    List<string> GetQueueItemNames();
    List<string> GetHistoryItemsNames();
    void CancelInstall(string appName);
    void PauseInstall();
    void ResumeInstall();
    Task StopProcessing();
    Task LoadPendingInstalls();
    Task<(double DownloadBytes, double InstallBytes)> GetGameDownloadInstallSizes(
        string appName, CancellationToken cancellationToken = default);
}

public sealed record StoreProduct(string ProviderId, string Title, string ProductId, string? ImageUrl);
public sealed record StoreDestination(string ProviderId, Uri Uri);

public interface ILauncherStore
{
    string DisplayName { get; }
    Uri HomeUri { get; }
    Uri CreateSearchUri(string query);
    Uri CreateProductUri(string productId);
    Task<IReadOnlyList<StoreProduct>> SearchStore(string query, CancellationToken cancellationToken = default);
}
