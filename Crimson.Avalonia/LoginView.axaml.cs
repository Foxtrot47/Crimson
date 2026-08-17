using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Presentation;

namespace Crimson.Avalonia;

public sealed partial class LoginView : UserControl
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string ReadAuthorizationCodeScript = """
        (() => {
            const text = document.body?.innerText?.trim();
            if (!text) return null;
            try {
                const value = JSON.parse(text);
                return value.authorizationCode ?? null;
            } catch {
                const match = text.match(/"authorizationCode"\s*:\s*"([A-Za-z0-9_-]+)"/);
                return match ? match[1] : null;
            }
        })()
        """;
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
    private readonly EpicAuthorizationCodeGate _authorizationCodeGate = new();

    public LoginView()
    {
        AvaloniaXamlLoader.Load(this);
        _loginWebView = this.FindControl<NativeWebView>("LoginWebView")!;
        var redirect = $"https://www.epicgames.com/id/api/redirect?clientId={ClientId}&responseType=code";
        _loginWebView.Source = new Uri(
            $"https://www.epicgames.com/id/login?redirectUrl={Uri.EscapeDataString(redirect)}");
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
        if (e.Request.AbsolutePath.Equals("/id/api/redirect", StringComparison.OrdinalIgnoreCase))
            _loginWebView.IsVisible = false;
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || e.Request is null ||
            !EpicEndpointPolicy.IsAllowedLoginOrigin(e.Request.AbsoluteUri))
            return;
        _currentSource = e.Request;

        try
        {
            await _loginWebView.InvokeScript(LoginScript);
            var result = await _loginWebView.InvokeScript(ReadAuthorizationCodeScript);
            if (_authorizationCodeGate.TryAccept(
                    _currentSource.AbsoluteUri,
                    result,
                    out var authorizationCode))
                await CompleteAuthorizationAsync(authorizationCode);
            else if (!_loginWebView.IsVisible)
                _loginWebView.IsVisible = true;
        }
        catch
        {
            _loginWebView.IsVisible = true;
        }
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

    private async Task CompleteAuthorizationAsync(string authorizationCode)
    {
        if (DataContext is not LoginViewModel viewModel)
            return;

        _loginWebView.IsVisible = false;
        await viewModel.AcceptAuthorizationCodeAsync(authorizationCode);
        if (viewModel.State != EpicAuthenticationState.LoggedIn)
            _loginWebView.IsVisible = true;
    }
}
