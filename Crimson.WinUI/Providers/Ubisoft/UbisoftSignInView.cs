using System;
using System.Threading;
using Crimson.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftSignInView : UserControl, ILauncherSignInView
{
    private readonly UbisoftAuthentication _auth = App.GetService<UbisoftAuthentication>();
    private readonly TextBox _email = new() { Header = "Email", PlaceholderText = "Ubisoft account email" };
    private readonly PasswordBox _password = new() { Header = "Password" };
    private readonly TextBox _code = new() { Header = "Two-factor code", Visibility = Visibility.Collapsed };
    private readonly CheckBox _remember = new() { Content = "Remember this account", IsChecked = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _submit = new() { Content = "Sign in" };
    private CancellationTokenSource? _lifetime;
    private bool _needsCode;
    public FrameworkElement Element => this;
    public UbisoftSignInView()
    {
        var panel = new StackPanel { Spacing = 12, Width = 360, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Connect Ubisoft", FontSize = 24 });
        panel.Children.Add(_email); panel.Children.Add(_password); panel.Children.Add(_code);
        panel.Children.Add(_remember); panel.Children.Add(_status); panel.Children.Add(_submit);
        panel.Children.Add(new TextBlock { Text = "Your password is sent to Ubisoft and is never saved. Remembered sessions are protected by Windows.", TextWrapping = TextWrapping.Wrap });
        Content = panel;
        _submit.Click += SignIn;
    }
    public void InitializeSignIn() { _lifetime = new(); _email.Focus(FocusState.Programmatic); }
    public void CloseSignIn()
    {
        _lifetime?.Cancel();
        _auth.CancelSignIn();
        _password.Password = "";
        _code.Text = "";
    }
    private async void SignIn(object sender, RoutedEventArgs args)
    {
        if (_lifetime is null) return;
        var token = _lifetime.Token;
        _submit.IsEnabled = false;
        _status.Text = "Signing in…";
        try
        {
            if (_needsCode) await _auth.CompleteTwoFactor(_code.Text.Trim(), token);
            else
            {
                if (string.IsNullOrWhiteSpace(_email.Text) || string.IsNullOrEmpty(_password.Password))
                { _status.Text = "Enter your email and password."; return; }
                _needsCode = await _auth.SignIn(_email.Text.Trim(), _password.Password, _remember.IsChecked == true, token);
                if (_needsCode)
                {
                    _code.Visibility = Visibility.Visible;
                    _email.IsEnabled = _password.IsEnabled = _remember.IsEnabled = false;
                    _status.Text = "Enter the code from your authenticator.";
                    _submit.Content = "Verify code";
                    _code.Focus(FocusState.Programmatic);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (UbisoftApiException error) { _status.Text = error.Message + " Check your credentials or try again later."; }
        catch (Exception) { _status.Text = "Ubisoft sign-in could not complete. Check your connection and try again."; }
        finally { _password.Password = ""; _submit.IsEnabled = true; }
    }
}
