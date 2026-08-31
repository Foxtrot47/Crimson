using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Crimson.Utils;

public static class WebViewEnvironmentFactory
{
    public static async Task<CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Crimson",
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
