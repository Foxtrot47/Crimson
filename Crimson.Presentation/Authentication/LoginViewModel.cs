using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;

namespace Crimson.Presentation;

public partial class LoginViewModel : ObservableObject, IActivatable
{
    private readonly IEpicAuthenticationService _authentication;
    private readonly INavigationService _navigation;
    private bool _active;

    [ObservableProperty]
    private EpicAuthenticationState _state = EpicAuthenticationState.LoggedOut;

    [ObservableProperty]
    private string? _displayName;

    [ObservableProperty]
    private string? _errorMessage;

    public LoginViewModel(
        IEpicAuthenticationService authentication,
        INavigationService navigation)
    {
        _authentication = authentication;
        _navigation = navigation;
    }

    public bool IsBusy => State is EpicAuthenticationState.Checking or EpicAuthenticationState.Authenticating;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_active)
            return;
        _active = true;
        _authentication.Changed += OnAuthenticationChanged;
        Apply(await _authentication.CheckAsync(cancellationToken));
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _authentication.Changed -= OnAuthenticationChanged;
    }

    public async Task AcceptExchangeCodeAsync(
        string exchangeCode,
        CancellationToken cancellationToken = default) =>
        Apply(await _authentication.LoginWithExchangeCodeAsync(exchangeCode, cancellationToken));

    public async Task AcceptAuthorizationCodeAsync(
        string authorizationCode,
        CancellationToken cancellationToken = default) =>
        Apply(await _authentication.LoginWithAuthorizationCodeAsync(
            authorizationCode,
            cancellationToken));

    [RelayCommand]
    private async Task LogoutAsync(CancellationToken cancellationToken)
    {
        await _authentication.LogoutAsync(cancellationToken);
        _navigation.Navigate(new LoginRoute());
    }

    partial void OnStateChanged(EpicAuthenticationState value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void OnAuthenticationChanged(object? sender, EpicAuthenticationSnapshot snapshot) => Apply(snapshot);

    private void Apply(EpicAuthenticationSnapshot snapshot)
    {
        State = snapshot.State;
        DisplayName = snapshot.DisplayName;
        ErrorMessage = snapshot.Error;
        _navigation.Navigate(snapshot.State == EpicAuthenticationState.LoggedIn
            ? new LibraryRoute()
            : new LoginRoute());
    }
}
