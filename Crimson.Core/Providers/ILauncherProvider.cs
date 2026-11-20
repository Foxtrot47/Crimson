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
/// <summary>Signed-in person behind a connection. Display only; never contains credentials.</summary>
public sealed record LauncherUser(string? DisplayName, string? AccountId = null, string? AvatarUrl = null)
{
    public bool HasName => !string.IsNullOrWhiteSpace(DisplayName);
}

public interface ILauncherAuthentication
{
    AuthenticationStatus AuthenticationStatus { get; }
    event Action<AuthenticationStatus>? StatusChanged;
    Task<AuthenticationStatus> CheckAuthStatus();
    Task Logout();

    /// <summary>Identity of the connected account, or null while signed out or unavailable.</summary>
    Task<LauncherUser?> GetUser(CancellationToken cancellationToken = default);
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

    /// <summary>Maps an owned addon to the game it belongs to; returns the input when unknown.</summary>
    string GetBaseGameAppName(string appName);

    string? LastLaunchError => null;
}

/// <summary>Provider-owned installer; does not prescribe manifests, slices or download protocols.</summary>
public interface ILauncherInstaller
{
    bool Supports(ActionType action);
    bool SupportsPause { get; }
    bool SupportsShortcuts { get; }
    InstallItem? CurrentInstall { get; }

    /// <summary>Normalized progress for the running job; providers that only publish <see cref="InstallItem"/> leave this null.</summary>
    OperationSnapshot? CurrentOperation { get; }
    IReadOnlyList<OperationSnapshot> GetQueueOperations();
    IReadOnlyList<OperationSnapshot> GetHistoryOperations();
    bool RetryFailedOperation(string appName);
    event Action<InstallItem>? InstallationStatusChanged;
    event Action<InstallItem>? InstallProgressUpdate;
    event Action<OperationSnapshot>? OperationUpdated;
    event Action<OperationSnapshot>? TransferRatesUpdated;
    event Action<OperationSnapshot>? OperationError;
    OperationSnapshot? GetActiveOperationForGame(string appName);
    bool TryAddToQueue(InstallItem item);
    void ReportPreparationFailure(string appName, ActionType action, OperationFailure failure);
    void ReportPreparationFailure(string appName, ActionType action, Exception exception);
    void ReportSecondaryFailure(InstallItem install, OperationFailure failure);
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

    /// <summary>Drops cached product details so the next lookup reaches the store again.</summary>
    void ClearCache();
}
