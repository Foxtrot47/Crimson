using System;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Presentation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson.Views;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class LoginPage : Page
{
    private readonly LoginViewModel _viewModel = App.GetService<LoginViewModel>();
    private readonly ILogger _log;
    private readonly IApplicationDirectories _directories = App.GetService<IApplicationDirectories>();
    private readonly EpicLoginMessageGate _loginMessageGate = new();
    private bool _initialized;

    public LoginPage()
    {
        this.InitializeComponent();
        _log = App.GetService<ILogger>();
    }
    private async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!EpicEndpointPolicy.IsAllowedLoginOrigin(e.Uri))
        {
            e.Cancel = true;
            _log.Warning("Login WebView blocked navigation to an unapproved origin");
            return;
        }

        const string jsCode = """
            window.ue = {
                signinprompt: {
                    requestexchangecodesignin: function(exchangeCode) {
                        const data = JSON.stringify({ type: 'set_exchange_code', code: exchangeCode });
                        window.chrome.webview.postMessage(data);
                    },
                },
                common: {
                    launchexternalurl: function(url) {
                        window.open(url, '_blank');
                    }
                }
            };
            """;

        await LoginWebView.ExecuteScriptAsync(jsCode);
    }
    private async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (!_loginMessageGate.TryAccept(e.Source, message, out var exchangeCode))
        {
            _log.Warning("Login WebView rejected an invalid or replayed message");
            return;
        }

        await _viewModel.AcceptExchangeCodeAsync(exchangeCode);
    }

    public async Task InitWebViewAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        _log.Information("InitWebView: WebView Initializing");
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            _directories.WebViewDataDirectory);
        await LoginWebView.EnsureCoreWebView2Async();
        LoginWebView.CoreWebView2.Settings.UserAgent = EpicLauncherWebLogin.UserAgent;
        LoginWebView.NavigationStarting += WebView_NavigationStarting;
        LoginWebView.WebMessageReceived += WebView_WebMessageReceived;

        LoginWebView.Source = EpicLauncherWebLogin.LoginUri;
    }
    public void CloseWebView()
    {
        if (!_initialized)
            return;
        _initialized = false;
        LoginWebView.NavigationStarting -= WebView_NavigationStarting;
        LoginWebView.WebMessageReceived -= WebView_WebMessageReceived;
        LoginWebView.Close();
    }
}
