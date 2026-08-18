using System.Diagnostics;
using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsGameProcessRunner : IGameProcessRunner
{
    public async Task RunAsync(
        GameProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                Arguments = startInfo.Arguments,
                WorkingDirectory = startInfo.WorkingDirectory,
                UseShellExecute = false
            }
        };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to launch '{startInfo.FileName}'.");
        await process.WaitForExitAsync(cancellationToken);
    }
}
