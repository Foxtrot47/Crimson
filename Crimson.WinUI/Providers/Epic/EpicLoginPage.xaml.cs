using System.Text.Json;
using System;
using Crimson.Core;
using Crimson.Utils;
using Crimson.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Serilog;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson.Providers.Epic;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class EpicLoginPage : Page, ILauncherSignInView
{
    public FrameworkElement Element => this;
    public void InitializeSignIn() => InitWebView();
    public void CloseSignIn() => CloseWebView();

    private readonly EpicAuthManager _authManager = App.GetService<EpicAuthManager>();
    private readonly ILogger _log;
    private readonly EpicLoginMessageGate _loginMessageGate = new();
    private const string EpicGamesLauncherVersion = "11.0.1-14907503+++Portal+Release-Live";
    private WebView2? _loginWebView;

    public EpicLoginPage()
    {
        this.InitializeComponent();
        _log = App.GetService<ILogger>();
    }
    private async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
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
            CloseWebView();

            var webView = new WebView2();
            WebViewHost.Children.Add(webView);
            _loginWebView = webView;

            await WebViewEnvironmentFactory.WaitForLoadedAsync(webView);
            if (!ReferenceEquals(_loginWebView, webView)) return;

            var environment = await WebViewEnvironmentFactory.CreateAsync(EpicLauncherProvider.ProviderId);
            if (!ReferenceEquals(_loginWebView, webView)) return;
            await webView.EnsureCoreWebView2Async(environment);
            if (!ReferenceEquals(_loginWebView, webView)) return;
            if (webView.CoreWebView2 is null)
            {
                _log.Error("InitWebView: CoreWebView2 was still null after initialisation");
                return;
            }

            webView.CoreWebView2.Settings.UserAgent = $"EpicGamesLauncher/{EpicGamesLauncherVersion}";

            webView.CoreWebView2.CookieManager.DeleteAllCookies();
            webView.NavigationStarting += WebView_NavigationStarting;
            webView.WebMessageReceived += WebView_WebMessageReceived;

            webView.Source = new Uri("https://www.epicgames.com/id/login");
        }
        catch (Exception ex)
        {
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
