using System.Diagnostics;
using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsGameProcessRunner : IGameProcessRunner
{
    public async Task RunAsync(
        LaunchPlan launchPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchPlan);
        var processStartInfo = new ProcessStartInfo
        {
            FileName = launchPlan.FileName,
            WorkingDirectory = launchPlan.WorkingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in launchPlan.Arguments)
            processStartInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in launchPlan.Environment)
            processStartInfo.Environment[name] = value;

        using var process = new Process { StartInfo = processStartInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to launch '{launchPlan.FileName}'.");
        await process.WaitForExitAsync(cancellationToken);
    }
}
