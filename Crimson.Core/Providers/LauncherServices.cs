using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Providers;

/// <summary>Independent connections to store accounts; Crimson has no active launcher.</summary>
public sealed class LauncherAuthentication
{
    private readonly LauncherRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger? _log;
    private readonly object _statusGate = new();
    private readonly Dictionary<string, AuthenticationStatus> _lastStatus = new(StringComparer.Ordinal);
    public IReadOnlyList<LauncherAccount> Accounts { get; }
    public event Action<string, AuthenticationStatus>? StatusChanged;
    public event Action<string>? SignInRequested;

    public LauncherAuthentication(LauncherRegistry registry, IUiDispatcher dispatcher, ILogger? log = null)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _log = log;
        Accounts = registry.Providers.Select(provider => new LauncherAccount(provider.Id, provider.DisplayName,
            provider.Authentication.AuthenticationStatus)).ToList().AsReadOnly();
        foreach (var provider in registry.Providers)
        {
            _lastStatus[provider.Id] = provider.Authentication.AuthenticationStatus;
            provider.Authentication.StatusChanged += status => Publish(provider.Id, status);
        }
    }

    private void Publish(string providerId, AuthenticationStatus status)
    {
        lock (_statusGate)
        {
            if (_lastStatus[providerId] == status) return;
            _lastStatus[providerId] = status;
        }
        _dispatcher.TryEnqueue(() => Accounts.Single(account => account.ProviderId == providerId).Status = status);
        StatusChanged?.Invoke(providerId, status);
    }

    public async Task<AuthenticationStatus> CheckAuthStatus(string providerId)
    {
        var provider = _registry.Get(providerId);
        try
        {
            var status = await provider.Authentication.CheckAuthStatus();
            Publish(providerId, status);
            return status;
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "Could not check account for {ProviderId}", providerId);
            Publish(providerId, AuthenticationStatus.LoginFailed);
            return AuthenticationStatus.LoginFailed;
        }
    }

    public Task CheckAllAuthStatuses() => Task.WhenAll(_registry.Providers.Select(provider => CheckAuthStatus(provider.Id)));
    public Task Logout(string providerId) => _registry.Get(providerId).Authentication.Logout();
    public void RequestSignIn(string providerId)
    {
        if (_registry.Get(providerId).Authentication.AuthenticationStatus == AuthenticationStatus.LoggedIn) return;
        SignInRequested?.Invoke(providerId);
    }
}

/// <summary>One combined library. Account failures and logout affect only that provider's entries.</summary>
public sealed class LauncherLibrary : ILauncherLibrary
{
    private readonly LauncherRegistry _registry;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<LauncherGame>> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    public event Action<IEnumerable<LauncherGame>>? LibraryUpdated;
    public event Action<LauncherGame>? GameStatusUpdated;
    public IReadOnlyList<LauncherGame> CachedGames { get { lock (_gate) return Snapshot(); } }

    public LauncherLibrary(LauncherRegistry registry, ILogger? log = null)
    {
        _registry = registry;
        _log = log;
        foreach (var provider in registry.Providers)
        {
            _sessions[provider.Id] = 0;
            _refreshGates[provider.Id] = new(1, 1);
            provider.Library.LibraryUpdated += games => PublishProvider(provider, games);
            provider.Library.GameStatusUpdated += game =>
            {
                var valid = ValidateGame(provider, game);
                if (provider.Authentication.AuthenticationStatus != AuthenticationStatus.LoggedIn) return;
                lock (_gate)
                {
                    if (_libraries.TryGetValue(provider.Id, out var games))
                    {
                        var index = games.FindIndex(existing => existing.Id == valid.Id);
                        if (index >= 0) games[index] = valid;
                    }
                }
                GameStatusUpdated?.Invoke(valid);
            };
            provider.Authentication.StatusChanged += status =>
            {
                if (status is not AuthenticationStatus.LoggedOut and not AuthenticationStatus.LoginFailed) return;
                lock (_gate)
                {
                    _sessions[provider.Id]++;
                    _libraries.Remove(provider.Id);
                }
                NotifyLibraryUpdated();
            };
        }
    }

    public async Task<IEnumerable<LauncherGame>> GetLibraryData(bool forceUpdate = false)
    {
        await Task.WhenAll(_registry.Providers.Select(provider => Refresh(provider, forceUpdate)));
        lock (_gate) return Snapshot();
    }

    private async Task Refresh(ILauncherProvider provider, bool forceUpdate)
    {
        await _refreshGates[provider.Id].WaitAsync();
        try
        {
            int session;
            lock (_gate) session = _sessions[provider.Id];
            if (provider.Authentication.AuthenticationStatus != AuthenticationStatus.LoggedIn) return;
            var games = ValidateGames(provider, await provider.Library.GetLibraryData(forceUpdate));
            lock (_gate)
            {
                if (session != _sessions[provider.Id] ||
                    provider.Authentication.AuthenticationStatus != AuthenticationStatus.LoggedIn) return;
                _libraries[provider.Id] = games;
            }
            NotifyLibraryUpdated();
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "Could not refresh library for {ProviderId}", provider.Id);
        }
        finally { _refreshGates[provider.Id].Release(); }
    }

    private void PublishProvider(ILauncherProvider provider, IEnumerable<LauncherGame> games)
    {
        var valid = ValidateGames(provider, games);
        lock (_gate)
        {
            if (provider.Authentication.AuthenticationStatus != AuthenticationStatus.LoggedIn) return;
            _libraries[provider.Id] = valid;
        }
        NotifyLibraryUpdated();
    }

    private void NotifyLibraryUpdated()
    {
        lock (_gate) LibraryUpdated?.Invoke(Snapshot());
    }

    // Called under _gate; sort/filter belongs to presentation, never to a selected store.
    private List<LauncherGame> Snapshot() => _registry.Providers
        .Where(provider => provider.Authentication.AuthenticationStatus == AuthenticationStatus.LoggedIn)
        .SelectMany(provider => _libraries.GetValueOrDefault(provider.Id) ?? []).ToList();

    public LauncherGame? GetGameInfo(string appName)
    {
        var provider = Resolve(appName);
        var game = provider.Library.GetGameInfo(GameId.Parse(appName).ToString());
        return game is null ? null : ValidateGame(provider, game);
    }

    public List<LauncherGame> GetDlcsForGame(string appName)
    {
        var provider = Resolve(appName);
        return ValidateGames(provider, provider.Library.GetDlcsForGame(GameId.Parse(appName).ToString()));
    }

    public async Task<bool> LaunchApp(string appName)
    {
        var id = GameId.Parse(appName);
        var provider = _registry.Get(id.ProviderId);
        if (provider.Authentication.AuthenticationStatus != AuthenticationStatus.LoggedIn ||
            provider.Library.GetGameInfo(id.ToString()) is not { CanLaunch: true }) return false;
        // Licenses, IPC, helper processes and their lifetimes remain provider-owned.
        return await provider.Library.LaunchApp(id.ToString());
    }

    public void InvalidateCache()
    {
        foreach (var provider in _registry.Providers) provider.Library.InvalidateCache();
    }
    private ILauncherProvider Resolve(string appName) => _registry.Get(GameId.Parse(appName).ProviderId);
    private static List<LauncherGame> ValidateGames(ILauncherProvider provider, IEnumerable<LauncherGame> games) =>
        games.Select(game => ValidateGame(provider, game)).ToList();
    private static LauncherGame ValidateGame(ILauncherProvider provider, LauncherGame game) =>
        game.Id.ProviderId == provider.Id ? game : throw new InvalidOperationException("Provider returned a foreign game identity.");
}

