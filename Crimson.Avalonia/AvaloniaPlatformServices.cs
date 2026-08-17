using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Crimson.Presentation;

namespace Crimson.Avalonia;

public sealed class AvaloniaFolderPickerService(Func<TopLevel?> topLevel) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(
        string? suggestedPath,
        CancellationToken cancellationToken = default)
    {
        var owner = topLevel() ?? throw new InvalidOperationException("No active Avalonia window exists.");
        var options = new FolderPickerOpenOptions
        {
            Title = "Select a folder",
            AllowMultiple = false
        };
        if (!string.IsNullOrWhiteSpace(suggestedPath) && Path.IsPathRooted(suggestedPath))
        {
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(
                new Uri(Path.GetFullPath(suggestedPath)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        return folders.Count == 1 ? folders[0].Path.LocalPath : null;
    }
}

public sealed class DesktopPathLauncher : IExternalPathLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(fullPath) { UseShellExecute = true };
        }
        else
        {
            startInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(fullPath);
        }

        Process.Start(startInfo);
        return Task.CompletedTask;
    }
}
