using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;

namespace Crimson.Providers;

public sealed class LauncherAccount(string providerId, string displayName, AuthenticationStatus status) : ObservableObject
{
    public string ProviderId { get; } = providerId;
    public string DisplayName { get; } = displayName;
    private AuthenticationStatus _status = status;
    public AuthenticationStatus Status
    {
        get => _status;
        internal set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public bool IsConnected => Status == AuthenticationStatus.LoggedIn;
    public bool CanConnect => Status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed;
    public string StatusText => Status switch
    {
        AuthenticationStatus.LoggedIn => "Connected",
        AuthenticationStatus.Checking => "Checking account…",
        AuthenticationStatus.LoginFailed => "Sign-in failed. Try connecting again.",
        _ => "Not connected"
    };
}
