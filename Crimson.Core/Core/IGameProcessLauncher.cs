using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Crimson.Core;

/// <summary>
/// A running game process. <see cref="Exited"/> completes only when the process and every
/// child process it spawned have exited, so save uploads after a play session capture the
/// final state even for games whose launcher executable hands off to the real game binary.
/// Disposing stops tracking; on implementations that wait on kernel handles, disposing also
/// completes <see cref="Exited"/> even if the process is still running.
/// </summary>
public interface IGameProcessHandle : IDisposable
{
    int Id { get; }

    Task Exited { get; }
}

/// <summary>
/// Starts game processes. The platform default tracks only the spawned process; on Windows
/// the process is assigned to a job object so the whole process tree is tracked.
/// Only FileName, Arguments and WorkingDirectory of the provided ProcessStartInfo are
/// guaranteed to be honored; platform implementations may ignore the rest.
/// </summary>
public interface IGameProcessLauncher
{
    IGameProcessHandle? Start(ProcessStartInfo startInfo);
}

public sealed class DefaultGameProcessLauncher : IGameProcessLauncher
{
    public IGameProcessHandle? Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            return null;
        }

        return new DefaultHandle(process);
    }

    private sealed class DefaultHandle(Process process) : IGameProcessHandle
    {
        public int Id => process.Id;
        public Task Exited => process.WaitForExitAsync();
        public void Dispose() => process.Dispose();
    }
}
