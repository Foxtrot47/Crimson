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

public sealed record UbisoftEntitlement(uint ProductId, string Title, string Type, string? ApplicationId, bool Playable);
public interface IUbisoftRunningGame : IAsyncDisposable { Task Completion { get; } }
public interface IUbisoftRuntime
{
    Task<IUbisoftRunningGame> Start(UbisoftSession session, UbisoftEntitlement game,
        UbisoftInstalledGame installation, UbisoftConfigurationStore configuration, CancellationToken token);
}

public sealed class UbisoftLibrary : ILauncherLibrary
{
    private readonly UbisoftAuthentication _auth;
    private readonly UbisoftApi _api;
    private readonly UbisoftConfigurationStore _configuration;
    private readonly IUbisoftRuntime _runtime;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private List<UbisoftEntitlement>? _entitlements;
    private IUbisoftRunningGame? _running;
    private uint? _playing;
    private int _epoch;
    private readonly object _lifetimeSync = new();
    private CancellationTokenSource _runtimeLifetime = new();
    public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
    public event Action<LauncherGame>? GameStatusUpdated;
    public IReadOnlyList<UbisoftEntitlement> Entitlements => _entitlements?.ToArray() ?? [];
    public string? LastLaunchError { get; private set; }
    public UbisoftLibrary(UbisoftAuthentication auth, UbisoftApi api, UbisoftConfigurationStore configuration, IUbisoftRuntime runtime, ILogger log)
    {
        _auth = auth; _api = api; _configuration = configuration; _runtime = runtime; _log = log;
        configuration.Changed += () => LibraryUpdated?.Invoke(Snapshot());
        auth.StatusChanged += status =>
        {
            if (status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed)
            {
                Interlocked.Increment(ref _epoch);
                lock (_lifetimeSync) _runtimeLifetime.Cancel();
                _entitlements = null;
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
        if (epoch != Volatile.Read(ref _epoch) || !ReferenceEquals(session, _auth.Session)) return [];
        _entitlements = entitlements;
        var snapshot = Snapshot();
        LibraryUpdated?.Invoke(snapshot);
        return snapshot;
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
            if (string.IsNullOrWhiteSpace(title)) title = $"Ubisoft {type} {id}";
            var playable = item.TryGetProperty("playable", out var p) && p.ValueKind == JsonValueKind.True;
            var applicationId = Guid.TryParse(Text("applicationId"), out var app) && app != Guid.Empty ? app.ToString() : null;
            results[id] = new(id, title, type, applicationId, playable);
        }
        return results.Values.ToList();
    }
    private List<LauncherGame> Snapshot() => (_entitlements ?? []).Select(ToGame).ToList();
    private LauncherGame ToGame(UbisoftEntitlement entitlement)
    {
        var installation = _configuration.Current.Games.FirstOrDefault(g => g.ProductId == entitlement.ProductId);
        return new LauncherGame
        {
            Id = new GameId("ubisoft", entitlement.ProductId.ToString(CultureInfo.InvariantCulture)),
            AppTitle = installation?.Title ?? entitlement.Title,
            IsDownloadableContent = entitlement.Type == "addon",
            CanLaunch = entitlement.Playable && entitlement.Type == "game" && entitlement.ApplicationId is not null && installation is not null,
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
    public LauncherGame? GetGameInfo(string appName) => _entitlements?.FirstOrDefault(g => g.ProductId == NativeId(appName)) is { } game ? ToGame(game) : null;
    public List<LauncherGame> GetDlcsForGame(string appName) { NativeId(appName); return []; } // API does not supply parent relationships
    public void InvalidateCache() { Interlocked.Increment(ref _epoch); _entitlements = null; }
    public async Task<bool> LaunchApp(string appName)
    {
        await _launchGate.WaitAsync();
        try
        {
            LastLaunchError = null;
            if (_running is not null) throw new InvalidOperationException("A Ubisoft game is already running in Crimson.");
            await _auth.RequireSession();
            // Refresh entitlement immediately before requesting live tickets.
            await GetLibraryData(true);
            var entitlement = _entitlements?.FirstOrDefault(e => e.ProductId == NativeId(appName));
            if (entitlement is null || !ToGame(entitlement).CanLaunch) throw new InvalidOperationException("Configure an installed, playable Ubisoft game first.");
            var installation = _configuration.Current.Games.Single(g => g.ProductId == entitlement.ProductId);
            UbisoftConfigurationStore.ValidateGame(installation);
            CancellationToken lifetime;
            lock (_lifetimeSync)
            {
                if (_auth.AuthenticationStatus != AuthenticationStatus.LoggedIn) throw new InvalidOperationException("Connect your Ubisoft account again.");
                _runtimeLifetime.Dispose();
                _runtimeLifetime = new CancellationTokenSource();
                lifetime = _runtimeLifetime.Token;
            }
            var session = _auth.Session ?? throw new InvalidOperationException("Connect your Ubisoft account again.");
            var running = await _runtime.Start(session, entitlement, installation, _configuration, lifetime);
            if (lifetime.IsCancellationRequested) { await running.DisposeAsync(); lifetime.ThrowIfCancellationRequested(); }
            _running = running;
            _playing = entitlement.ProductId;
            GameStatusUpdated?.Invoke(ToGame(entitlement));
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
            try { if (ReferenceEquals(_running, running)) { _running = null; _playing = null; GameStatusUpdated?.Invoke(ToGame(game)); } }
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
