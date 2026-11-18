using System;
using System.Linq;
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
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountInitials));
            OnPropertyChanged(nameof(HasAccountName));
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
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountInitials));
            OnPropertyChanged(nameof(HasAccountName));
        }
    }

    // Launchers name the account for the signed-in person; the store name is the fallback.
    public string AccountName =>
        IsConnected && User is { HasName: true } user ? user.DisplayName! : DisplayName;

    public bool HasAccountName => IsConnected && User is { HasName: true };

    public string AccountInitials => new((AccountName.Trim() is { Length: > 0 } name ? name : DisplayName)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
        .Select(part => char.ToUpperInvariant(part[0])).ToArray());

    public bool IsConnected => Status == AuthenticationStatus.LoggedIn;
    public bool CanConnect => Status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed;
    public string StatusText => Status switch
    {
        AuthenticationStatus.LoggedIn => UiStrings.Connected,
        AuthenticationStatus.Checking => UiStrings.CheckingAccount,
        AuthenticationStatus.LoginFailed => UiStrings.SignInFailed,
        _ => UiStrings.NotConnected
    };
}
