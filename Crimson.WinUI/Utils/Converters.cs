using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.Converters;
using Crimson.Core;
using Crimson.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Crimson.Utils;

public partial class BoolToVisibilityConverter : BoolToObjectConverter
{
    public BoolToVisibilityConverter()
    {
        TrueValue = Visibility.Visible;
        FalseValue = Visibility.Collapsed;
    }
}

public partial class BoolToInverseVisibilityConverter : BoolToObjectConverter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoolToInverseVisibilityConverter"/> class.
    /// </summary>
    public BoolToInverseVisibilityConverter()
    {
        TrueValue = Visibility.Collapsed;
        FalseValue = Visibility.Visible;
    }
}

public partial class ImageUrlConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string imageUrl || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return null!;

        if (!uri.IsFile)
        {
            // Trust is provider-owned: any registered store may publish its own artwork hosts.
            if (!ArtworkTrust.IsTrusted(uri)) return null!;
            // Remote artwork goes through the shared cache so every page reuses downloads and works offline.
            var remote = new BitmapImage();
            _ = CachedImageLoader.LoadAsync(remote, imageUrl, App.GetService<IArtworkCache>(), DispatcherQueue.GetForCurrentThread());
            return remote;
        }

        var image = new BitmapImage();
        _ = CachedImageLoader.LoadFileAsync(image, uri.LocalPath);
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Draws the store mark a provider registers for its library entries.</summary>
public partial class ProviderLogoConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string appName) return null;
        string providerId;
        try { providerId = Crimson.Providers.GameId.Parse(appName).ProviderId; }
        catch (ArgumentException) { return null; }
        try
        {
            var logo = App.GetService<Crimson.Providers.LauncherFrontendRegistry>().Get(providerId).StoreLogo;
            return string.IsNullOrWhiteSpace(logo) ? null : new BitmapImage(new Uri(logo));
        }
        catch (Exception) { return null; } // An unregistered provider simply has no mark.
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Asks the registered providers whether an artwork URL is theirs to serve.</summary>
public static class ArtworkTrust
{
    private static IReadOnlyList<Crimson.Providers.IProviderContentPolicy>? _policies;

    public static bool IsTrusted(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        _policies ??= App.GetServices<Crimson.Providers.IProviderContentPolicy>().ToList();
        return _policies.Any(policy => policy.AllowsArtworkUri(uri.ToString()));
    }
}

/// <summary>Loads artwork cache files into bitmaps; WinUI cannot read packaged app data paths by URI.</summary>
public static class CachedImageLoader
{
    public static async Task LoadFileAsync(BitmapImage image, string path, Uri? fallback = null)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var stream = file.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to decode cached artwork from {Path}; removing it so the cache can retry", path);
            try { File.Delete(path); }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                Log.Warning(deleteException, "Failed to remove invalid cached artwork");
            }
            if (fallback is not null) image.UriSource = fallback;
        }
    }

    /// <summary>
    /// Resolves <paramref name="url"/> through the artwork cache and loads the result on the UI thread.
    /// If the cache cannot provide a file, the image loads <paramref name="url"/> directly so its
    /// ImageOpened or ImageFailed event still fires.
    /// </summary>
    public static async Task LoadAsync(BitmapImage image, string? url, IArtworkCache cache, DispatcherQueue dispatcher)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var remote)) return;
        string? cached = null;
        try { cached = await cache.GetImageUriAsync(url).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning(ex, "Failed to resolve cached artwork"); }

        dispatcher.TryEnqueue(() =>
        {
            if (Uri.TryCreate(cached, UriKind.Absolute, out var local) && local.IsFile)
                _ = LoadFileAsync(image, local.LocalPath, remote);
            else if (ArtworkTrust.IsTrusted(remote))
                image.UriSource = remote;
        });
    }
}

public partial class GamePrimaryActionGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        GamePrimaryAction.Install => "\uE896",
        GamePrimaryAction.Play => "\uE768",
        GamePrimaryAction.Update => "\uE777",
        GamePrimaryAction.Repair => "\uE90F",
        _ => string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public partial class DriveSpaceColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool canInstall)
        {
            // Get colors from theme resources to respect light/dark mode
            if (canInstall)
            {
                return Application.Current.Resources["SystemFillColorSuccessBrush"] as SolidColorBrush;
            }
            else
            {
                return Application.Current.Resources["SystemFillColorCriticalBrush"] as SolidColorBrush;
            }
        }

        // Return default color if input is invalid
        return Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
