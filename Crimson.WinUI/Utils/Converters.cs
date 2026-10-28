using System;
using System.IO;
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

public class BoolToVisibilityConverter : BoolToObjectConverter
{
    public BoolToVisibilityConverter()
    {
        TrueValue = Visibility.Visible;
        FalseValue = Visibility.Collapsed;
    }
}

public class BoolToInverseVisibilityConverter : BoolToObjectConverter
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

public class ImageUrlConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string imageUrl || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return null!;

        if (!uri.IsFile)
            return EpicEndpointPolicy.IsAllowedContentUri(uri) ? new BitmapImage(uri) : null!;

        var image = new BitmapImage();
        _ = CachedImageLoader.LoadFileAsync(image, uri.LocalPath);
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>Loads artwork cache files into bitmaps; WinUI cannot read packaged app data paths by URI.</summary>
public static class CachedImageLoader
{
    public static async Task LoadFileAsync(BitmapImage image, string path)
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
        }
    }

    /// <summary>Resolves <paramref name="url"/> through the artwork cache and loads the result on the UI thread.</summary>
    public static async Task LoadAsync(BitmapImage image, string? url, IArtworkCache cache, DispatcherQueue dispatcher)
    {
        var uri = await cache.GetImageUriAsync(url).ConfigureAwait(false);
        if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var local) || !local.IsFile) return;
        dispatcher.TryEnqueue(() => _ = LoadFileAsync(image, local.LocalPath));
    }
}

public class GamePrimaryActionGlyphConverter : IValueConverter
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

public class DriveSpaceColorConverter : IValueConverter
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
