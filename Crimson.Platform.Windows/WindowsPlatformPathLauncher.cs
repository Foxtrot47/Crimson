using System.Diagnostics;
using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsPlatformPathLauncher : IPlatformPathLauncher
{
    public Task OpenDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
