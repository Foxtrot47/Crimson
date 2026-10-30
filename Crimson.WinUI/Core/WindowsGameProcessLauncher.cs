using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Serilog;

namespace Crimson.WinUI.Core;

/// <summary>
/// Starts game processes inside a Windows job object so process-tree exit is observed, not
/// just the launcher executable. Games like Control ship a bootstrapper (Control.exe) that
/// spawns the real renderer (Control_DX12.exe) and exits immediately; waiting on the
/// spawned process alone would report the session as over seconds after launch and upload
/// stale saves. A job handle is signaled once the last process in the tree — including
/// children spawned later — has exited.
/// The process is created suspended and assigned to the job before it is resumed, so no
/// child can escape tracking. If job assignment is not allowed (the process is already in
/// a restrictive job) the launcher falls back to tracking the root process only.
/// Only FileName, Arguments and WorkingDirectory of the provided ProcessStartInfo are used.
/// </summary>
public sealed class WindowsGameProcessLauncher : IGameProcessLauncher
{
    private readonly ILogger? _log;

    public WindowsGameProcessLauncher(ILogger? log = null) => _log = log;

    public IGameProcessHandle? Start(ProcessStartInfo startInfo)
    {
        var startupInfo = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>() };
        // The command line is reparsed by the child; quote the executable path. CreateProcess
        // may mutate the command line buffer in place, so pass a writable StringBuilder.
        var commandLine = new StringBuilder($"\"{startInfo.FileName}\" {startInfo.Arguments}");
        if (!NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                NativeMethods.CREATE_SUSPENDED,
                IntPtr.Zero,
                string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                ref startupInfo,
                out var processInfo))
        {
            _log?.Warning(
                "CreateProcess failed for a game launch: {Error}",
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return null;
        }

        try
        {
            var tracking = JobTreeTracking.TryCreate(processInfo.hProcess, _log);
            if (NativeMethods.ResumeThread(processInfo.hThread) == uint.MaxValue)
            {
                // Never leave a permanently suspended process behind: kill it and report the
                // launch as failed so the caller cleans up ownership tokens and launch state.
                _log?.Warning(
                    "ResumeThread failed after a game launch: {Error}",
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
                tracking?.Dispose();
                NativeMethods.TerminateProcess(processInfo.hProcess, 1);
                NativeMethods.CloseHandle(processInfo.hThread);
                NativeMethods.CloseHandle(processInfo.hProcess);
                return null;
            }

            NativeMethods.CloseHandle(processInfo.hThread);
            processInfo.hThread = IntPtr.Zero;
            return new WindowsGameProcessHandle(
                (int)processInfo.dwProcessId, processInfo.hProcess, tracking);
        }
        catch
        {
            if (processInfo.hThread != IntPtr.Zero)
                NativeMethods.CloseHandle(processInfo.hThread);
            NativeMethods.CloseHandle(processInfo.hProcess);
            throw;
        }
    }

    private sealed class WindowsGameProcessHandle : IGameProcessHandle
    {
        private IntPtr _processHandle;
        private readonly JobTreeTracking? _tracking;
        private readonly SignalWait? _rootWait;

        public WindowsGameProcessHandle(int id, IntPtr processHandle, JobTreeTracking? tracking)
        {
            Id = id;
            _processHandle = processHandle;
            _tracking = tracking;
            // The job is signaled when the whole tree exits; without a job, wait on the
            // root process handle. Both waits run on dedicated threads, so no pool thread
            // is parked for the length of the game session.
            if (tracking is not null)
            {
                Exited = tracking.TreeExited;
            }
            else
            {
                _rootWait = new SignalWait(processHandle);
                Exited = _rootWait.Completion;
            }
        }

        public int Id { get; }
        public Task Exited { get; }

        public void Dispose()
        {
            _rootWait?.Dispose();
            _tracking?.Dispose();
            // The value is exchanged out before closing, so a second Dispose can never
            // close a handle the OS may already have recycled for something else.
            var handle = Interlocked.Exchange(ref _processHandle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Completes a task when a kernel handle is signaled. The wait runs on a dedicated
    /// background thread instead of a thread-pool worker, so a long game session never
    /// parks a pool thread. Disposing completes the task so callers stop tracking.
    /// </summary>
    private sealed class SignalWait : IDisposable
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public SignalWait(IntPtr handle)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    NativeMethods.WaitForSingleObject(handle, NativeMethods.INFINITE);
                }
                finally
                {
                    _completion.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "Crimson game process wait"
            };
            _thread.Start();
        }

        public Task Completion => _completion.Task;

        public void Dispose() => _completion.TrySetResult();
    }

    /// <summary>
    /// Owns the job object and its completion port. The port is watched on a dedicated
    /// background thread until the job reports zero active processes; disposing closes
    /// the port, which also ends the wait. A dedicated thread is used instead of a
    /// thread-pool worker because a game session can last hours. The job never gets a
    /// kill-on-close limit: releasing the handles must not affect the running game.
    /// </summary>
    private sealed class JobTreeTracking : IDisposable
    {
        private IntPtr _jobHandle;
        private IntPtr _portHandle;
        private readonly TaskCompletionSource _treeExited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TreeExited => _treeExited.Task;

        private JobTreeTracking(IntPtr jobHandle, IntPtr portHandle)
        {
            _jobHandle = jobHandle;
            _portHandle = portHandle;
        }

        public static JobTreeTracking? TryCreate(IntPtr processHandle, ILogger? log)
        {
            var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero || job == NativeMethods.INVALID_HANDLE_VALUE)
                return Failed(log, "CreateJobObject", Marshal.GetLastWin32Error());

            var port = IntPtr.Zero;
            try
            {
                port = NativeMethods.CreateIoCompletionPort(
                    NativeMethods.INVALID_HANDLE_VALUE, IntPtr.Zero, UIntPtr.Zero, 1);
                if (port == IntPtr.Zero || port == NativeMethods.INVALID_HANDLE_VALUE)
                {
                    var portError = Marshal.GetLastWin32Error();
                    NativeMethods.CloseHandle(job);
                    return Failed(log, "CreateIoCompletionPort", portError);
                }

                var association = new NativeMethods.JOBOBJECT_ASSOCIATE_COMPLETION_PORT
                {
                    CompletionKey = job,
                    CompletionPort = port
                };
                var associationSize = Marshal.SizeOf<NativeMethods.JOBOBJECT_ASSOCIATE_COMPLETION_PORT>();
                var associationPtr = Marshal.AllocHGlobal(associationSize);
                try
                {
                    Marshal.StructureToPtr(association, associationPtr, false);
                    if (!NativeMethods.SetInformationJobObject(
                            job,
                            NativeMethods.JobObjectAssociateCompletionPortInformation,
                            associationPtr,
                            (uint)associationSize))
                    {
                        var associationError = Marshal.GetLastWin32Error();
                        NativeMethods.CloseHandle(port);
                        NativeMethods.CloseHandle(job);
                        return Failed(log, "SetInformationJobObject", associationError);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(associationPtr);
                }

                if (!NativeMethods.AssignProcessToJobObject(job, processHandle))
                {
                    var assignError = Marshal.GetLastWin32Error();
                    NativeMethods.CloseHandle(port);
                    NativeMethods.CloseHandle(job);
                    return Failed(log, "AssignProcessToJobObject", assignError);
                }

                var tracking = new JobTreeTracking(job, port);
                tracking.StartWaitThread();
                return tracking;
            }
            catch
            {
                // Any failure past job creation (marshaling, thread start) must not leak
                // handles or leave them owned by a half-built instance.
                if (port != IntPtr.Zero)
                    NativeMethods.CloseHandle(port);
                NativeMethods.CloseHandle(job);
                throw;
            }
        }

        private static JobTreeTracking? Failed(ILogger? log, string step, int error)
        {
            log?.Warning(
                "Game process tree tracking is unavailable ({Step}: {Error}); falling back to root process tracking",
                step,
                new Win32Exception(error).Message);
            return null;
        }

        private void StartWaitThread()
        {
            var thread = new Thread(WaitForActiveProcessZero)
            {
                IsBackground = true,
                Name = "Crimson game process tree wait"
            };
            thread.Start();
        }

        private void WaitForActiveProcessZero()
        {
            // Capture the port: Dispose exchanges it out and closes it to wake this wait.
            var port = _portHandle;
            try
            {
                // Job notifications deliver the JOB_OBJECT_MSG_* identifier in the
                // lpNumberOfBytesTransferred field of the completion packet.
                while (NativeMethods.GetQueuedCompletionStatus(
                           port, out var messageId, out _, out _, NativeMethods.INFINITE))
                {
                    if (messageId == NativeMethods.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO)
                        break;
                }
            }
            finally
            {
                _treeExited.TrySetResult();
            }
        }

        public void Dispose()
        {
            // Closing the port first wakes the waiter with an error so the background
            // thread cannot outlive the handles it uses. Both closes are idempotent:
            // the value is exchanged out first, so a second Dispose or a racing waiter
            // can never close a handle the OS may already have recycled.
            var port = Interlocked.Exchange(ref _portHandle, IntPtr.Zero);
            if (port != IntPtr.Zero)
                NativeMethods.CloseHandle(port);
            var job = Interlocked.Exchange(ref _jobHandle, IntPtr.Zero);
            if (job != IntPtr.Zero)
                NativeMethods.CloseHandle(job);
        }
    }

    private static class NativeMethods
    {
        public const uint CREATE_SUSPENDED = 0x4;
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4;
        public const int JobObjectAssociateCompletionPortInformation = 7;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool inheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateIoCompletionPort(
            IntPtr fileHandle, IntPtr existingCompletionPort, UIntPtr completionKey, uint numberOfConcurrentThreads);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetQueuedCompletionStatus(
            IntPtr completionPort,
            out uint lpNumberOfBytesTransferred,
            out UIntPtr lpCompletionKey,
            out IntPtr lpOverlapped,
            uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            public IntPtr CompletionKey;
            public IntPtr CompletionPort;
        }
    }
}
