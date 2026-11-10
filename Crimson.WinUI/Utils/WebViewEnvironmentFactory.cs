using System.IO;
using System.Threading.Tasks;
using System;
using Crimson.Providers;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Crimson.Utils;

public static class WebViewEnvironmentFactory
{
    public static async Task<CoreWebView2Environment> CreateAsync(string providerId)
    {
        var userDataFolder = Path.Combine(
            ProviderDataPaths.ForProvider(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Crimson"), providerId),
            "webview2");
        Directory.CreateDirectory(userDataFolder);

        return await CoreWebView2Environment.CreateWithOptionsAsync(
            string.Empty, userDataFolder, new CoreWebView2EnvironmentOptions());
    }

    public static Task WaitForLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded)
            return Task.CompletedTask;

        var loaded = new TaskCompletionSource();

        void OnLoaded(object sender, RoutedEventArgs args)
        {
            element.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }

        element.Loaded += OnLoaded;
        return loaded.Task;
    }
}
