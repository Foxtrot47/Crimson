using System;
using System.Text.Json;
using Crimson.Core;
using Crimson.Utils;
using Microsoft.UI.Xaml;
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
    private readonly AuthManager _authManager = App.GetService<AuthManager>();
    private readonly ILogger _log;
    private readonly EpicLoginMessageGate _loginMessageGate = new();
    private const string EpicGamesLauncherVersion = "11.0.1-14907503+++Portal+Release-Live";
    private WebView2? _loginWebView;

    public LoginPage()
    {
        this.InitializeComponent();
        _log = App.GetService<ILogger>();
    }
    private async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Injected on every navigation, and navigation is never cancelled. The login flow
        // legitimately visits captcha and third-party SSO origins, and ExecuteScriptAsync here
        // targets the *currently loaded* document rather than e.Uri, so filtering on e.Uri would
        // skip the Epic document that actually needs the shim. Containment is enforced where the
        // secret arrives instead: EpicLoginMessageGate checks the origin of the inbound message.
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

        if (_loginWebView is null)
            return;

        await _loginWebView.ExecuteScriptAsync(jsCode);
    }
    private async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (!_loginMessageGate.TryAccept(e.Source, message, out var exchangeCode))
        {
            _log.Warning("Login WebView rejected an invalid or replayed message");
            return;
        }

        await _authManager.DoExchangeLogin(exchangeCode);
    }
    public async void InitWebView()
    {
        _log.Information("InitWebView: WebView Initializing");
        try
        {
            // Signing out and back in lands here a second time, and the previous control
            // has already been closed, so start from a fresh one.
            CloseWebView();

            var webView = new WebView2();
            WebViewHost.Children.Add(webView);
            _loginWebView = webView;

            // The control has to be loaded before the browser process can be created.
            // Called on a control added moments ago, EnsureCoreWebView2Async otherwise
            // returns having done nothing and leaves CoreWebView2 null.
            await WebViewEnvironmentFactory.WaitForLoadedAsync(webView);

            var environment = await WebViewEnvironmentFactory.CreateAsync();
            await webView.EnsureCoreWebView2Async(environment);
            if (webView.CoreWebView2 is null)
            {
                _log.Error("InitWebView: CoreWebView2 was still null after initialisation");
                return;
            }

            webView.CoreWebView2.Settings.UserAgent = $"EpicGamesLauncher/{EpicGamesLauncherVersion}";

            // Epic's own session cookie outlives a sign-out, and the login page would
            // silently accept it and hand back a fresh exchange code for the account that
            // just left. Tokens are persisted separately, so nothing is lost by dropping it.
            webView.CoreWebView2.CookieManager.DeleteAllCookies();
            webView.NavigationStarting += WebView_NavigationStarting;
            webView.WebMessageReceived += WebView_WebMessageReceived;

            webView.Source = new Uri("https://www.epicgames.com/id/login");
        }
        catch (Exception ex)
        {
            // Nothing awaits this method, so an escaping exception would terminate the app.
            _log.Error(ex, "InitWebView: failed to initialise the login WebView");
        }
    }
    public void CloseWebView()
    {
        if (_loginWebView is null)
            return;

        _loginWebView.NavigationStarting -= WebView_NavigationStarting;
        _loginWebView.WebMessageReceived -= WebView_WebMessageReceived;
        _loginWebView.Close();
        WebViewHost.Children.Remove(_loginWebView);
        _loginWebView = null;
    }
}
