using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Resources;

namespace Crimson.Providers;

/// <summary>
/// One store connection as the shell presents it: who is signed in, the connection state and the
/// actions that change it. Providers never appear here by name, so any number can be registered.
/// </summary>
public sealed class LauncherAccount : ObservableObject
{
    public LauncherAccount(string providerId, string displayName, AuthenticationStatus status,
        Action? connect = null, Action? disconnect = null)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _status = status;
        ConnectCommand = new RelayCommand(() => connect?.Invoke());
        DisconnectCommand = new RelayCommand(() => disconnect?.Invoke());
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public IRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    private AuthenticationStatus _status;
    public AuthenticationStatus Status
    {
        get => _status;
        internal set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(Summary));
        }
    }
    private LauncherUser? _user;

    /// <summary>Identity of the signed-in person, refreshed when the connection changes.</summary>
    public LauncherUser? User
    {
        get => _user;
        internal set
        {
            if (!SetProperty(ref _user, value)) return;
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Who is signed in when the store names the account, otherwise the connection state.</summary>
    public string Summary => IsConnected && User is { HasName: true } user
        ? UiStrings.Format(UiStrings.SignedInAsFormat, user.DisplayName!)
        : StatusText;

    public bool IsConnected => Status == AuthenticationStatus.LoggedIn;
    public bool CanConnect => Status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed;
    private string StatusText => Status switch
    {
        AuthenticationStatus.LoggedIn => UiStrings.Connected,
        AuthenticationStatus.Checking => UiStrings.CheckingAccount,
        AuthenticationStatus.LoginFailed => UiStrings.SignInFailed,
        _ => UiStrings.NotConnected
    };
}
