using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Password sign-in for Ubisoft in two steps: credentials, then the authenticator code when the
/// account asks for one. The provider performs the request; this view owns presentation, validation
/// and turning failures into something a person can act on.
/// </summary>
public sealed partial class UbisoftSignInView : UserControl, ILauncherSignInView
{
    private const string AccountPage = "https://account.ubisoft.com/";

    private readonly UbisoftAuthentication _auth = App.GetService<UbisoftAuthentication>();
    private CancellationTokenSource? _lifetime;
    private bool _codeStep;
    private bool _busy;

    public UbisoftSignInView() => InitializeComponent();

    public FrameworkElement Element => this;

    // A form, not a browser surface: it sizes to whichever step is showing.
    public LauncherSignInSize PreferredSize => new(440, null);

    public void InitializeSignIn()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _codeStep = false;
        // Remembered address only; the password is never stored.
        EmailBox.Text = _auth.LastEmail;
        PasswordInput.Password = string.Empty;
        CodeBox.Text = string.Empty;
        RememberBox.IsChecked = true;
        ShowCredentials();
        SetError(null);
        SetBusy(false);
        // Returning accounts type only the password.
        if (EmailBox.Text.Length > 0) PasswordInput.Focus(FocusState.Programmatic);
        else EmailBox.Focus(FocusState.Programmatic);
    }

    public void CloseSignIn()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        _auth.CancelSignIn();
        // Credentials must not survive a closed dialog in memory.
        PasswordInput.Password = string.Empty;
        CodeBox.Text = string.Empty;
        EmailBox.Text = string.Empty;
    }

    private async void Submit_Click(object sender, RoutedEventArgs args) => await SubmitAsync();

    private async void Field_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Enter or VirtualKey.GamepadA)) return;
        args.Handled = true;
        await SubmitAsync();
    }

    private void Input_Changed(object sender, RoutedEventArgs args) => SetError(null);

    private void UseAnotherAccount_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        _auth.CancelSignIn();
        _codeStep = false;
        CodeBox.Text = string.Empty;
        PasswordInput.Password = string.Empty;
        ShowCredentials();
        SetError(null);
        EmailBox.Focus(FocusState.Programmatic);
    }

    private async void ManageAccount_Click(object sender, RoutedEventArgs args)
    {
        try { await Launcher.LaunchUriAsync(new Uri(AccountPage)); }
        catch (Exception) { } // Opening a browser is best-effort.
    }

    private async Task SubmitAsync()
    {
        if (_busy || _lifetime is not { } lifetime) return;
        var token = lifetime.Token;

        if (_codeStep)
        {
            var code = CodeBox.Text.Trim();
            if (code.Length == 0)
            {
                RejectInput(UiStrings.UbisoftCodeRequired, CodeBox);
                return;
            }
            await RunAsync(UiStrings.UbisoftVerifying, async () =>
            {
                await _auth.CompleteTwoFactor(code, token);
                return true;
            }, token);
            return;
        }

        var email = EmailBox.Text.Trim();
        var password = PasswordInput.Password;
        if (email.Length == 0 || password.Length == 0)
        {
            RejectInput(UiStrings.UbisoftEmailPasswordRequired, email.Length == 0 ? EmailBox : PasswordInput);
            return;
        }

        var (ok, needsCode) = await RunAsync(UiStrings.UbisoftSigningIn,
            () => _auth.SignIn(email, password, RememberBox.IsChecked == true, token), token);
        if (!ok || !needsCode || token.IsCancellationRequested) return;

        _codeStep = true;
        PasswordInput.Password = string.Empty; // No longer needed once the challenge is issued.
        ShowCodeStep();
        CodeBox.Focus(FocusState.Programmatic);
    }

    /// <summary>One provider call behind one busy state; failures become advice, never protocol detail.</summary>
    private async Task<(bool Ok, T? Value)> RunAsync<T>(string busyLabel, Func<Task<T>> call, CancellationToken token)
    {
        SetError(null);
        SetBusy(true, busyLabel);
        try
        {
            var value = await call();
            return token.IsCancellationRequested ? (false, default) : (true, value);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (false, default);
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) SetError(UbisoftSignInFailure.Describe(error, _codeStep));
            return (false, default);
        }
        finally
        {
            if (!token.IsCancellationRequested) SetBusy(false);
        }
    }

    private void RejectInput(string message, Control field)
    {
        SetError(message);
        field.Focus(FocusState.Programmatic);
    }

    private void ShowCredentials()
    {
        CredentialsPanel.Visibility = Visibility.Visible;
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        EmailBox.IsEnabled = PasswordInput.IsEnabled = RememberBox.IsEnabled = true;
        SubmitButton.Content = UiStrings.SignIn;
    }

    private void ShowCodeStep()
    {
        CredentialsPanel.Visibility = Visibility.Collapsed;
        TwoFactorPanel.Visibility = Visibility.Visible;
        SubmitButton.Content = UiStrings.VerifyCode;
    }

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.IsEnabled = !busy;
        EmailBox.IsEnabled = PasswordInput.IsEnabled = RememberBox.IsEnabled = !busy && !_codeStep;
        CodeBox.IsEnabled = !busy;
        // The button doubles as the progress label, so restore its action text when the work ends.
        SubmitButton.Content = busy ? label ?? UiStrings.SignIn
            : _codeStep ? UiStrings.VerifyCode : UiStrings.SignIn;
    }

    private void SetError(string? message)
    {
        ErrorBar.IsOpen = !string.IsNullOrEmpty(message);
        ErrorBar.Message = message ?? string.Empty;
    }
}
