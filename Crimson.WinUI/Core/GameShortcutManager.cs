using Crimson.Providers;
using Crimson.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;

namespace Crimson.Core;

public sealed class GameShortcutManager : IGameShortcutManager
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _log;
    private readonly string _desktopDirectory;
    private readonly string _startMenuDirectory;
    private readonly string _iconDirectory;
    private readonly IReadOnlyDictionary<string, IProviderContentPolicy> _policies;

    public GameShortcutManager(HttpClient httpClient, ILogger log, IEnumerable<IProviderContentPolicy>? policies = null)
        : this(
            httpClient,
            log,
            policies,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "Crimson Games"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Crimson",
                "shortcut-icons"))
    {
    }

    internal GameShortcutManager(
        HttpClient httpClient,
        ILogger log,
        IEnumerable<IProviderContentPolicy>? policies,
        string desktopDirectory,
        string startMenuDirectory,
        string iconDirectory)
    {
        _httpClient = httpClient;
        _log = log;
        _desktopDirectory = desktopDirectory;
        _startMenuDirectory = startMenuDirectory;
        _iconDirectory = iconDirectory;
        _policies = (policies ?? []).ToDictionary(policy => policy.ProviderId, StringComparer.Ordinal);
    }

    internal GameShortcutManager(
        HttpClient httpClient,
        ILogger log,
        string desktopDirectory,
        string startMenuDirectory,
        string iconDirectory)
        : this(httpClient, log, null, desktopDirectory, startMenuDirectory, iconDirectory)
    {
    }

    public async Task CreateAsync(LauncherGame game, GameShortcutLocation location, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetShortcutPath(game, location);
        RequireAvailableShortcutPath(path, game.AppName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var iconPath = await GetIconPathAsync(game, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CreateShellLink(path, game, iconPath);
    }

    public void Remove(LauncherGame game)
    {
        var errors = new[]
        {
            RemoveOwnedShortcut(GetShortcutPath(game, GameShortcutLocation.StartMenu), game.AppName),
            RemoveOwnedShortcut(GetShortcutPath(game, GameShortcutLocation.Desktop), game.AppName),
            TryDeleteFile(Path.Combine(_iconDirectory, "games", GetIconFileName(game.Id)), game.AppName)
        }.OfType<Exception>().ToArray();
        if (errors.Length > 0) throw new AggregateException("Shortcut cleanup failed.", errors);
    }

    private string GetShortcutPath(LauncherGame game, GameShortcutLocation location)
    {
        var directory = location == GameShortcutLocation.Desktop
            ? _desktopDirectory
            : _startMenuDirectory;
        return Path.Combine(directory, GameShortcutNaming.GetShortcutFileName(game.Id.ProviderId == GameId.LegacyProviderId ? game.AppTitle : $"{game.AppTitle} ({game.Id.ProviderId})"));
    }

    private Exception? RemoveOwnedShortcut(string path, string appName) =>
        IsOwnedShortcut(path, appName) ? TryDeleteFile(path, appName) : null;

    private static void RequireAvailableShortcutPath(string path, string appName)
    {
        if (File.Exists(path) && !IsOwnedShortcut(path, appName))
            throw new IOException("An unrelated file already occupies the requested shortcut path.");
    }

    private static bool IsOwnedShortcut(string path, string appName)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            return ReadShortcutOwnership(path, appName);
        }
        catch (COMException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ReadShortcutOwnership(string path, string appName)
    {
        using var link = ShellLinkInterop.Create();
        link.Load(path);
        var target = link.GetPath();
        var targetsCrimson = string.Equals(target, GetPackagedAliasPath(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        return targetsCrimson && MatchesOwnedArguments(link.GetArguments(), appName);
    }

    /// <summary>Accepts the current qualified command line and the unqualified Epic form written by older builds.</summary>
    internal static bool MatchesOwnedArguments(string actual, string appName)
    {
        if (string.Equals(actual, GameLaunchRequest.CreateCommandLineArgument(appName), StringComparison.Ordinal))
            return true;
        var id = GameId.Parse(appName);
        return id.ProviderId == GameId.LegacyProviderId &&
            string.Equals(actual, GameLaunchRequest.CreateCommandLineArgument(id.LocalId), StringComparison.Ordinal);
    }

    // Epic keeps its historical icon names so existing shortcuts are not orphaned.
    private static string GetIconFileName(GameId id) => id.ProviderId == GameId.LegacyProviderId
        ? GameShortcutNaming.GetIconFileName(id.LocalId) : ProviderDataPaths.GameFileKey(id) + ".ico";

    private Exception? TryDeleteFile(string path, string appName)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to remove shortcut artifact {Path} for {AppName}", path, appName);
            return ex;
        }
    }

    private async Task<string> GetIconPathAsync(LauncherGame game, CancellationToken cancellationToken)
    {
        var gameIconDirectory = Path.Combine(_iconDirectory, "games");
        Directory.CreateDirectory(gameIconDirectory);
        var iconPath = Path.Combine(gameIconDirectory, GetIconFileName(game.Id));
        if (File.Exists(iconPath))
            return iconPath;

        var imageUrl = FindImageUrl(game);
        if (!string.IsNullOrWhiteSpace(imageUrl) && _policies.TryGetValue(game.Id.ProviderId, out var policy))
        {
            try
            {
                var imageUri = policy.RequireArtworkUri(imageUrl);
                await using var source = await _httpClient.GetStreamAsync(imageUri, cancellationToken);
                await CreateIconFileAsync(source, iconPath);
                return iconPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to create shortcut icon for {AppName}", game.AppName);
            }
        }

        return await GetCrimsonIconPathAsync();
    }

    private async Task<string> GetCrimsonIconPathAsync()
    {
        var iconPath = Path.Combine(_iconDirectory, "Crimson.ico");
        if (File.Exists(iconPath))
            return iconPath;

        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.scale-200.png");
            await using var source = File.OpenRead(logoPath);
            await CreateIconFileAsync(source, iconPath);
            return iconPath;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to create the Crimson shortcut icon");
            return Environment.ProcessPath ?? string.Empty;
        }
    }

    private static string? FindImageUrl(LauncherGame game) => game.CoverImageUrl ?? game.LandscapeImageUrl;

    internal static async Task CreateIconFileAsync(Stream source, string path)
    {
        var (png, size) = await CreateSquarePngAsync(source);
        WriteIcon(path, png, size);
    }

    private static async Task<(byte[] Png, uint Size)> CreateSquarePngAsync(Stream source)
    {
        using var bufferedSource = new MemoryStream();
        await source.CopyToAsync(bufferedSource);
        bufferedSource.Position = 0;
        using var sourceStream = bufferedSource.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var edge = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
        var size = Math.Min(edge, 256u);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var croppedPixels = CropSquare(
            pixels.DetachPixelData(),
            decoder.PixelWidth,
            decoder.PixelHeight,
            edge);

        using var png = new MemoryStream();
        using var output = png.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            edge,
            edge,
            decoder.DpiX > 0 ? decoder.DpiX : 96,
            decoder.DpiY > 0 ? decoder.DpiY : 96,
            croppedPixels);
        encoder.BitmapTransform.ScaledWidth = size;
        encoder.BitmapTransform.ScaledHeight = size;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        return (png.ToArray(), size);
    }

    private static byte[] CropSquare(byte[] source, uint width, uint height, uint edge)
    {
        var edgeLength = checked((int)edge);
        var rowBytes = checked(edgeLength * 4);
        var result = new byte[checked(rowBytes * edgeLength)];
        var x = (width - edge) / 2;
        var y = (height - edge) / 2;
        for (var row = 0; row < edgeLength; row++)
        {
            var sourceOffset = checked((int)(((y + (uint)row) * width + x) * 4));
            Buffer.BlockCopy(source, sourceOffset, result, row * rowBytes, rowBytes);
        }

        return result;
    }

    private static void WriteIcon(string path, byte[] png, uint size)
    {
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(size >= 256 ? (byte)0 : (byte)size);
        writer.Write(size >= 256 ? (byte)0 : (byte)size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)png.Length);
        writer.Write((uint)22);
        writer.Write(png);
    }

    internal static void CreateShellLink(
        string path,
        LauncherGame game,
        string iconPath,
        bool? packaged = null)
    {
        RequireAvailableShortcutPath(path, game.AppName);
        using var link = ShellLinkInterop.Create();
        link.SetPath((packaged ?? IsPackaged())
            ? GetPackagedAliasPath()
            : Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable."));
        link.SetArguments(GameLaunchRequest.CreateCommandLineArgument(game.AppName));
        link.SetDescription(UiStrings.Format(UiStrings.ShortcutDescriptionFormat, game.AppTitle));
        if (!string.IsNullOrEmpty(iconPath))
            link.SetIconLocation(iconPath, 0);
        link.Save(path);
    }

    internal static string GetPackagedAliasPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "WindowsApps",
        "crimson.exe");

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current.Id.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
