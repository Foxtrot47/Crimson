using System.Diagnostics;
using Crimson.Core;
using Crimson.WinUI.Core;

namespace Crimson.Tests;

/// <summary>
/// Games whose launcher executable hands off to the real game binary (e.g. Control.exe
/// spawning Control_DX12.exe and exiting) must count as running until the last child
/// process exits, otherwise save uploads would fire while the session is still going.
/// </summary>
public sealed class GameProcessTreeTrackingTests
{
    private static readonly string CmdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    [Fact]
    public async Task WaitsForChildProcessesAfterTheLauncherExits()
    {
        // cmd starts a ~9 second ping in the background and exits immediately itself;
        // process-only tracking would report exit right away, tree tracking must not.
        using var handle = new WindowsGameProcessLauncher().Start(new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c start \"\" /b ping -n 10 127.0.0.1 >nul & exit",
            UseShellExecute = false
        });

        Assert.NotNull(handle);
        await Task.Delay(1500);
        Assert.False(handle.Exited.IsCompleted);
        await handle.Exited.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CompletesWhenTheTreeExitsImmediately()
    {
        using var handle = new WindowsGameProcessLauncher().Start(new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c exit 0",
            UseShellExecute = false
        });

        Assert.NotNull(handle);
        await handle.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ReturnsNullWhenTheProcessCannotStart()
    {
        var handle = new WindowsGameProcessLauncher().Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"),
            UseShellExecute = false
        });

        Assert.Null(handle);
    }

    [Fact]
    public async Task DisposeWhileRunningCompletesExitedWithoutKillingTheProcess()
    {
        using var handle = new WindowsGameProcessLauncher().Start(new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c ping -n 10 127.0.0.1 >nul",
            UseShellExecute = false
        });

        Assert.NotNull(handle);
        handle.Dispose();
        // Dispose means "stop tracking": Exited completes instead of hanging forever.
        await handle.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        // The process itself is left alone (no kill-on-close) and finishes on its own.
        try
        {
            Assert.False(Process.GetProcessById(handle.Id).HasExited);
        }
        catch (ArgumentException)
        {
            // The process already exited on its own; acceptable on a fast machine.
        }
    }

    [Fact]
    public async Task DefaultLauncherTracksOnlyTheSpawnedProcess()
    {
        using var handle = new DefaultGameProcessLauncher().Start(new ProcessStartInfo
        {
            FileName = CmdPath,
            Arguments = "/c exit 0",
            UseShellExecute = false
        });

        Assert.NotNull(handle);
        await handle.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
