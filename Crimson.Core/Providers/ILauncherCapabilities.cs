using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Providers;

/// <summary>Shell-facing catalog data for one owned game. Providers decide which fields they publish.</summary>
public sealed record LauncherGameDetails(
    string? CoverImageUrl = null,
    string? LandscapeImageUrl = null,
    string? DeveloperName = null,
    string? Description = null,
    string? InstalledVersion = null,
    DateTimeOffset? LastUpdatedAt = null);

/// <summary>
/// Optional provider capability: playtime, running state, catalog details and public store
/// metadata for its own games. The shared game page hides anything a provider does not implement.
/// Every app name is a qualified <see cref="GameId"/> string.
/// </summary>
public interface IGameDetailsSource
{
    event Action<string, bool>? RunningStateChanged;
    bool IsRunning(string appName);
    bool AllowsMultipleInstances(string appName);
    LauncherGameDetails? GetGameDetails(string appName);
    Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default);
    Task<StoreProductDetails?> GetStoreProductDetails(string appName, CancellationToken cancellationToken = default);

    /// <summary>Public store page for the game, or null when the provider has no store entry.</summary>
    Uri? GetStorePageUri(string appName);
}

/// <summary>Optional provider capability: cloud-save state for its own games.</summary>
public interface IGameCloudSaves
{
    event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;
    bool Supports(string appName);
    CloudSaveSyncStatus GetSyncStatus(string appName);
}
