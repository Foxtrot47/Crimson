using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Crimson.Providers.Ubisoft;

/// <summary>The native starter expects an inherited wait event. Inherit only that handle.</summary>
internal sealed class UbisoftProcess : IDisposable
{
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private SafeProcessHandle? _process;
    internal void SignalReady() => _ready.Set();
    internal void Start(UbisoftInstalledGame game)
    {
        var attributes = IntPtr.Zero;
        var handles = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!SetHandleInformation(_ready.SafeWaitHandle, 1, 1)) throw new Win32Exception();
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handles, _ready.SafeWaitHandle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startup = new StartupInfoEx { Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            // Configuration is explicit user input. No shell or URI launcher is involved.
            var command = new StringBuilder("\"" + game.Executable + "\"" + (string.IsNullOrWhiteSpace(game.Arguments) ? "" : " " + game.Arguments));
            if (!CreateProcess(game.Executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x80000,
                IntPtr.Zero, game.InstallDirectory, ref startup, out var info)) throw new Win32Exception();
            CloseHandle(info.Thread);
            _process = new SafeProcessHandle(info.Process, true);
        }
        finally
        {
            if (attributes != IntPtr.Zero) { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }
    internal async Task WaitForExit(CancellationToken token)
    {
        if (_process is null) throw new InvalidOperationException("Game process has not started.");
        while (true)
        {
            var result = WaitForSingleObject(_process, 0);
            if (result == 0) return;
            if (result != 258) throw new Win32Exception();
            await Task.Delay(250, token);
        }
    }
    public void Dispose() { _process?.Dispose(); _ready.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
