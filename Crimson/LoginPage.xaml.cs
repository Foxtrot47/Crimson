using Crimson.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            this.InitializeComponent();
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
                };
            ";

            await LoginWebView.ExecuteScriptAsync(jsCode);
        }
        private async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString();
            var response = JsonSerializer.Deserialize<EpicLoginResponse>(message);
            await AuthManager.RequestTokens(response);
        }
        public async void InitWebView()
        {
            await LoginWebView.EnsureCoreWebView2Async();
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
}
