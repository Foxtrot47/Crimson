using System;

namespace Crimson.Core;

public enum AuthenticationStatus
{
    Checking,
    LoggedOut,
    LoggedIn,
    LoginFailed
}

public class AuthStatusChangedEventArgs(AuthenticationStatus newStatus) : EventArgs
{
    public AuthenticationStatus NewStatus { get; } = newStatus;
}
