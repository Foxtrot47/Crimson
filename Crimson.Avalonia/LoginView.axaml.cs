using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Presentation;

namespace Crimson.Avalonia;

public sealed partial class LoginView : UserControl
{
    private const string LoginScript = """
        window.ue = {
            signinprompt: {
                requestexchangecodesignin: function(exchangeCode) {
                    const data = JSON.stringify({ type: 'set_exchange_code', code: exchangeCode });
                    invokeCSharpAction(data);
                }
            },
            common: {
                launchexternalurl: function(url) { window.open(url, '_blank'); }
            }
        };
        """;
    private readonly NativeWebView _loginWebView;
    private readonly EpicLoginMessageGate _messageGate = new();
    private Uri? _currentSource;

    public LoginView()
    {
        AvaloniaXamlLoader.Load(this);
        _loginWebView = this.FindControl<NativeWebView>("LoginWebView")!;
        _loginWebView.Source = new Uri("https://www.epicgames.com/id/login");
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        var root = AppDataPaths.GetDefaultRoot();
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.UserDataFolder = Path.Combine(root, "webview2");
            windows.ProfileName = "CrimsonEpic";
            Directory.CreateDirectory(windows.UserDataFolder);
        }
        else if (e is LinuxWpeWebViewEnvironmentRequestedEventArgs linux)
        {
            linux.DataDirectory = Path.Combine(root, "webview");
            linux.CacheDirectory = Path.Combine(root, "cache", "webview");
            Directory.CreateDirectory(linux.DataDirectory);
            Directory.CreateDirectory(linux.CacheDirectory);
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is null || !EpicEndpointPolicy.IsAllowedLoginOrigin(e.Request.AbsoluteUri))
        {
            e.Cancel = true;
            return;
        }

        _currentSource = e.Request;
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || e.Request is null ||
            !EpicEndpointPolicy.IsAllowedLoginOrigin(e.Request.AbsoluteUri))
            return;
        _currentSource = e.Request;

        await _loginWebView.InvokeScript(LoginScript);
    }

    private async void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (DataContext is not LoginViewModel viewModel ||
            !_messageGate.TryAccept(_currentSource?.AbsoluteUri, e.Body, out var exchangeCode))
            return;

        _loginWebView.IsVisible = false;
        await viewModel.AcceptExchangeCodeAsync(exchangeCode);
        if (viewModel.State != EpicAuthenticationState.LoggedIn)
            _loginWebView.IsVisible = true;
    }

}
