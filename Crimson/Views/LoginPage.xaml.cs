using System;
using System.IO;
using System.Text.Json;
using Crimson.Core;
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
    private readonly AuthManager _authManager = DependencyResolver.Resolve<AuthManager>();
    private readonly ILogger _log;
    private const string EpicGamesLauncherVersion = "11.0.1-14907503+++Portal+Release-Live";

    public LoginPage()
    {
        this.InitializeComponent();
        _log = DependencyResolver.Resolve<ILogger>();
    }
    async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string jsCode = @"
                window.ue = {
                    signinprompt: {
                        requestexchangecodesignin: function(exchangeCode) {
                            var data = JSON.stringify({ type: 'set_exchange_code', code: exchangeCode });
                            window.chrome.webview.postMessage(data);
                        },
                    },
                    common: {
                        launchexternalurl: function(url) {
                            window.open(url, '_blank');
                        }
                    }
                };
            ";

        await LoginWebView.ExecuteScriptAsync(jsCode);
    }
    private async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        _log.Information("WebView_WebMessageReceived: Message {@message}", message);
        var response = JsonSerializer.Deserialize<EpicLoginResponse>(message);
        _authManager.DoExchangeLogin(response.Code);
    }
    public async void InitWebView()
    {
        _log.Information("InitWebView: WebView Initializing}");
        var userDataFolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crimson");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
        await LoginWebView.EnsureCoreWebView2Async();
        LoginWebView.CoreWebView2.Settings.UserAgent = $"EpicGamesLauncher/{EpicGamesLauncherVersion}";
        LoginWebView.NavigationStarting += WebView_NavigationStarting;
        LoginWebView.WebMessageReceived += WebView_WebMessageReceived;

        var targetUri = new Uri("https://www.epicgames.com/id/login");
        LoginWebView.Source = targetUri;
    }
    public void CloseWebView()
    {
        LoginWebView.Close();
    }
}
