using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Providers.Ubisoft;

/// <summary>One owned product. Display fields are filled from <see cref="UbisoftMetadata"/>.</summary>
public sealed record UbisoftEntitlement(uint ProductId, string Title, string Type, string? ApplicationId, bool Playable,
    string? SpaceId = null, string? CoverImageUrl = null, string? LandscapeImageUrl = null, uint ParentProductId = 0);
public interface IUbisoftRunningGame : IAsyncDisposable { Task Completion { get; } }
public interface IUbisoftRuntime
{
    Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftEntitlement game,
        UbisoftInstalledGame installation, UbisoftConfigurationStore configuration, CancellationToken token);
}

public sealed class UbisoftLibrary : ILauncherLibrary, IGameDetailsSource
{
    private readonly UbisoftAuthentication _auth;
    private readonly UbisoftApi _api;
    private readonly UbisoftMetadata _metadata;
    private readonly UbisoftConfigurationStore _configuration;
    private readonly IUbisoftRuntime _runtime;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private List<UbisoftEntitlement>? _entitlements;
    private Dictionary<uint, UbisoftInstalledGame> _installations = [];
    private IUbisoftRunningGame? _running;
    private uint? _playing;
    private int _epoch;
    private readonly object _lifetimeSync = new();
    private CancellationTokenSource _runtimeLifetime = new();
    public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
    public event Action<LauncherGame>? GameStatusUpdated;
    public event Action<string, bool>? RunningStateChanged;
    public IReadOnlyList<UbisoftEntitlement> Entitlements => _entitlements?.ToArray() ?? [];
    public string? LastLaunchError { get; private set; }
    public UbisoftLibrary(UbisoftAuthentication auth, UbisoftApi api, UbisoftMetadata metadata,
        UbisoftConfigurationStore configuration, IUbisoftRuntime runtime, ILogger log)
    {
        _auth = auth; _api = api; _metadata = metadata;
        _configuration = configuration; _runtime = runtime; _log = log;
        // A configuration change can add, move or remove an installation, so re-resolve before publishing.
        configuration.Changed += () =>
        {
            ResolveInstallations();
            LibraryUpdated?.Invoke(Snapshot());
        };
        auth.StatusChanged += status =>
        {
            if (status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed)
            {
                Interlocked.Increment(ref _epoch);
                lock (_lifetimeSync) _runtimeLifetime.Cancel();
                _entitlements = null;
                _installations = [];
                _ = StopRuntime();
            }
        };
    }
    public async Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false)
    {
        await _configuration.Load();
        if (_entitlements is not null && !forceUpdate) return Snapshot();
        var session = await _auth.RequireSession();
        var epoch = Volatile.Read(ref _epoch);
        var json = await _api.Get(session, "/v1/profiles/me/global/ubiconnect/entitlement/api/entitlements");
        var entitlements = ParseEntitlements(json);
        var metadata = await _metadata.Resolve(session, entitlements, CancellationToken.None);
        if (epoch != Volatile.Read(ref _epoch) || !ReferenceEquals(session, _auth.Session)) return [];
        _entitlements = entitlements
            .Select(entitlement => metadata.TryGetValue(entitlement.ProductId, out var details)
                ? entitlement with
                {
                    Title = details.Title, CoverImageUrl = details.CoverImageUrl,
                    LandscapeImageUrl = details.LandscapeImageUrl, ParentProductId = details.ParentProductId
                }
                : entitlement)
            .ToList();
        ResolveInstallations();
        var snapshot = Snapshot();
        LibraryUpdated?.Invoke(snapshot);
        return snapshot;
    }
    /// <summary>
    /// Matches owned games against the client's install records. Explicit configuration entries still
    /// win, so a moved installation can be corrected without touching the client's registry.
    /// </summary>
    private void ResolveInstallations()
    {
        var overrides = _configuration.Current.Games.ToDictionary(game => game.ProductId);
        var resolved = new Dictionary<uint, UbisoftInstalledGame>();
        foreach (var entitlement in _entitlements ?? [])
        {
            if (entitlement.Type != "game") continue;
            // Only Crimson's own records count: installations it created or imported explicitly.
            var configured = overrides.GetValueOrDefault(entitlement.ProductId);
            if (configured is null) continue;
            var ownedAddons = (_entitlements ?? [])
                .Where(addon => addon.Type == "addon" && addon.ParentProductId == entitlement.ProductId)
                .Select(addon => addon.ProductId)
                .ToList();
            resolved[entitlement.ProductId] = configured with
            {
                Title = configured.Title,
                AddonValidationIds = configured.AddonValidationIds ?? ownedAddons
            };
        }
        _installations = resolved;
    }

    internal static List<UbisoftEntitlement> ParseEntitlements(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "items", "entitlements", "products", "games", "results" })
                if (json.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array) { json = array; break; }
        if (json.ValueKind != JsonValueKind.Array) throw new JsonException("Unknown Ubisoft entitlement response.");
        var results = new Dictionary<uint, UbisoftEntitlement>();
        foreach (var item in json.EnumerateArray())
        {
            if (!item.TryGetProperty("productId", out var pid) || !uint.TryParse(pid.ToString(), out var id) || id == 0) continue;
            string Text(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var type = Text("type");
            if (type is not "game" and not "addon") continue;
            var title = Text("name");
            if (string.IsNullOrWhiteSpace(title)) title = Text("displayName");
            var spaceId = Guid.TryParse(Text("spaceId"), out var space) && space != Guid.Empty ? space.ToString() : null;
            var playable = item.TryGetProperty("playable", out var p) && p.ValueKind == JsonValueKind.True;
            var applicationId = Guid.TryParse(Text("applicationId"), out var app) && app != Guid.Empty ? app.ToString() : null;
            results[id] = new(id, title, type, applicationId, playable, spaceId);
        }
        return results.Values.ToList();
    }
    private List<LauncherGame> Snapshot() => (_entitlements ?? []).Select(ToGame).ToList();
    private LauncherGame ToGame(UbisoftEntitlement entitlement)
    {
        var installation = _installations.GetValueOrDefault(entitlement.ProductId);
        return new LauncherGame
        {
            Id = new GameId("ubisoft", entitlement.ProductId.ToString(CultureInfo.InvariantCulture)),
            AppTitle = installation?.Title ?? entitlement.Title,
            CoverImageUrl = entitlement.CoverImageUrl,
            LandscapeImageUrl = entitlement.LandscapeImageUrl,
            IsDownloadableContent = entitlement.Type == "addon",
            // Playable means one launch attempt can start: installed, entitled and carrying an application id.
            // A missing runtime is reported by the runtime itself, with the registry value it expected.
            CanLaunch = entitlement.Playable && entitlement.Type == "game" && entitlement.ApplicationId is not null &&
                installation is not null,
            LocalAppState = installation is null ? null : new LauncherInstallation(
                _playing == entitlement.ProductId ? InstallState.Playing : InstallState.Installed, installation.InstallDirectory)
        };
    }
    private static uint NativeId(string appName)
    {
        var id = GameId.Parse(appName);
        if (id.ProviderId != "ubisoft" || !uint.TryParse(id.LocalId, out var productId)) throw new ArgumentException("Expected a Ubisoft product ID.");
        return productId;
    }
    public LauncherGame? GetGameInfo(string appName)
    {
        var productId = NativeId(appName);
        ResolveInstallations();
        return _entitlements?.FirstOrDefault(game => game.ProductId == productId) is { } game ? ToGame(game) : null;
    }
    public List<LauncherGame> GetDlcsForGame(string appName)
    {
        var productId = NativeId(appName);
        return (_entitlements ?? [])
            .Where(entitlement => entitlement.Type == "addon" && entitlement.ParentProductId == productId)
            .Select(ToGame)
            .ToList();
    }

    /// <summary>An addon's page belongs to its game; unknown parents keep their own identity.</summary>
    public string GetBaseGameAppName(string appName)
    {
        var productId = NativeId(appName);
        var parent = _entitlements?.FirstOrDefault(entitlement => entitlement.ProductId == productId)?.ParentProductId ?? 0;
        return parent == 0 || parent == productId
            ? appName
            : new GameId("ubisoft", parent.ToString(CultureInfo.InvariantCulture)).ToString();
    }
    public bool IsRunning(string appName) => _playing == NativeId(appName);

    // One Ubisoft title runs at a time, so a second launch stays locked while it plays.
    public bool AllowsMultipleInstances(string appName) { NativeId(appName); return false; }

    public LauncherGameDetails? GetGameDetails(string appName)
    {
        var entitlement = _entitlements?.FirstOrDefault(game => game.ProductId == NativeId(appName));
        if (entitlement is null) return null;
        return new LauncherGameDetails(entitlement.CoverImageUrl, entitlement.LandscapeImageUrl,
            Description: _metadata.FindCatalogGame(entitlement.SpaceId)?.Description);
    }

    public Task<long?> GetGamePlaytimeSeconds(string appName, CancellationToken token = default)
    {
        NativeId(appName);
        return Task.FromResult<long?>(null); // The public API does not publish playtime.
    }

    public Task<StoreProductDetails?> GetStoreProductDetails(string appName, CancellationToken token = default)
    {
        NativeId(appName);
        return Task.FromResult<StoreProductDetails?>(null);
    }

    public Uri? GetStorePageUri(string appName) { NativeId(appName); return null; }

    public void InvalidateCache() { Interlocked.Increment(ref _epoch); _entitlements = null; _installations = []; }
    public async Task<bool> LaunchApp(string appName)
    {
        await _launchGate.WaitAsync();
        try
        {
            LastLaunchError = null;
            if (_running is not null) throw new InvalidOperationException("A Ubisoft game is already running in Crimson.");
            var session = await _auth.RequireSession();
            var epoch = Volatile.Read(ref _epoch);
            // Refresh entitlement immediately before requesting live tickets.
            await GetLibraryData(true);
            var entitlement = _entitlements?.FirstOrDefault(e => e.ProductId == NativeId(appName));
            if (entitlement is null || !ToGame(entitlement).CanLaunch) throw new InvalidOperationException("Configure an installed, playable Ubisoft game first.");
            if (!_installations.TryGetValue(entitlement.ProductId, out var installation))
                throw new InvalidOperationException("Configure an installed, playable Ubisoft game first.");
            UbisoftConfigurationStore.ValidateGame(installation);
            CancellationToken lifetime;
            lock (_lifetimeSync)
            {
                if (_auth.AuthenticationStatus != AuthenticationStatus.LoggedIn || !ReferenceEquals(session, _auth.Session) || epoch != Volatile.Read(ref _epoch)) throw new InvalidOperationException("Connect your Ubisoft account again.");
                _runtimeLifetime.Dispose();
                _runtimeLifetime = new CancellationTokenSource();
                lifetime = _runtimeLifetime.Token;
            }
            var running = await _runtime.Start(session, entitlement, installation, _configuration, lifetime);
            if (lifetime.IsCancellationRequested) { await running.DisposeAsync(); lifetime.ThrowIfCancellationRequested(); }
            _running = running;
            _playing = entitlement.ProductId;
            GameStatusUpdated?.Invoke(ToGame(entitlement));
            RunningStateChanged?.Invoke(appName, true);
            _ = ObserveGame(_running, entitlement);
            return true;
        }
        catch (Exception error)
        {
            LastLaunchError = error is InvalidOperationException or System.IO.FileNotFoundException ? error.Message : $"Ubisoft launch failed ({error.GetType().Name}).";
            _log.Warning("Ubisoft launch failed: {Reason}", LastLaunchError);
            return false;
        }
        finally { _launchGate.Release(); }
    }
    private async Task ObserveGame(IUbisoftRunningGame running, UbisoftEntitlement game)
    {
        try { await running.Completion; }
        catch (Exception error)
        {
            LastLaunchError = error is InvalidOperationException ? error.Message : $"Ubisoft runtime failed ({error.GetType().Name}).";
            _log.Warning("Ubisoft runtime ended: {Reason}", LastLaunchError);
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
                    GameStatusUpdated?.Invoke(ToGame(game));
                    RunningStateChanged?.Invoke(ToGame(game).AppName, false);
                }
            }
            finally { _launchGate.Release(); }
        }
    }
    private async Task StopRuntime()
    {
        await _launchGate.WaitAsync();
        try { if (_running is not null) await _running.DisposeAsync(); }
        finally { _launchGate.Release(); }
    }
}
