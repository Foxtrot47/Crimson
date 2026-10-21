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

