using Crimson.Repository;

namespace Crimson.Core;

public enum EpicAuthenticationState
{
    LoggedOut,
    Checking,
    Authenticating,
    LoggedIn,
    Failed
}

public sealed record EpicAuthenticationSnapshot(
    EpicAuthenticationState State,
    string? DisplayName = null,
    string? Error = null);

public interface IEpicAuthenticationService : IAccessTokenProvider
{
    EpicAuthenticationSnapshot Snapshot { get; }

    event EventHandler<EpicAuthenticationSnapshot>? Changed;

    Task<EpicAuthenticationSnapshot> CheckAsync(CancellationToken cancellationToken = default);

    Task<EpicAuthenticationSnapshot> LoginWithExchangeCodeAsync(
        string exchangeCode,
        CancellationToken cancellationToken = default);

    Task<EpicAuthenticationSnapshot> LoginWithAuthorizationCodeAsync(
        string authorizationCode,
        CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);
}
