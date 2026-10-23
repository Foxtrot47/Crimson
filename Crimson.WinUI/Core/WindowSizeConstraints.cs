using System;
using System.Runtime.InteropServices;
using Serilog;

namespace Crimson.Core;

internal sealed class WindowSizeConstraints : IDisposable
{
    private const int MinimumWidthDip = 1497;
    private const int MinimumHeightDip = 801;
    private const uint GetMinMaxInfo = 0x0024;
    private const uint DpiChanged = 0x02E0;
    private const uint NonClientDestroy = 0x0082;
    private const uint NearestMonitor = 2;
    private const uint KeepPositionAndActivation = 0x0002 | 0x0004 | 0x0010;
    private const uint SubclassId = 1;
    private readonly SubclassProcedure _procedure;
    private readonly ILogger _log;
    private GCHandle _lifetime;
    private nint _window;

    internal WindowSizeConstraints(nint window, ILogger log)
    {
        if (window == 0) throw new ArgumentException("A valid window handle is required.", nameof(window));
        _window = window;
        _log = log;
        _procedure = HandleMessage;
        // Native subclass callbacks are not GC roots. Retain the callback owner until removal or destruction.
        _lifetime = GCHandle.Alloc(this);
        if (!SetWindowSubclass(window, _procedure, SubclassId, 0))
        {
            _lifetime.Free();
            throw new InvalidOperationException("The minimum window size could not be configured.");
        }
        EnsureMinimumSize();
    }

    internal static (int Width, int Height) CalculateMinimum(uint dpi, int workAreaWidth, int workAreaHeight)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var width = (int)Math.Min(int.MaxValue, Math.Ceiling(MinimumWidthDip * scale));
        var height = (int)Math.Min(int.MaxValue, Math.Ceiling(MinimumHeightDip * scale));
        return (
            workAreaWidth > 0 ? Math.Min(width, workAreaWidth) : width,
            workAreaHeight > 0 ? Math.Min(height, workAreaHeight) : height);
    }

    private (int Width, int Height) GetMinimum()
    {
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(_window, NearestMonitor), ref monitor))
            _log.Warning("The monitor work area could not be read for minimum window sizing.");
        return CalculateMinimum(GetDpiForWindow(_window), monitor.WorkArea.Right - monitor.WorkArea.Left,
            monitor.WorkArea.Bottom - monitor.WorkArea.Top);
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == NonClientDestroy)
        {
            RemoveWindowSubclass(window, _procedure, SubclassId);
            ReleaseLifetime();
        }
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == GetMinMaxInfo && lParam != 0)
        {
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var minimum = GetMinimum();
            bounds.MinimumTrackSize = new NativePoint { X = minimum.Width, Y = minimum.Height };
            Marshal.StructureToPtr(bounds, lParam, false);
            return 0;
        }
        if (message == DpiChanged) EnsureMinimumSize();
        return result;
    }

    private void EnsureMinimumSize()
    {
        if (_window == 0 || IsIconic(_window) || IsZoomed(_window)) return;
        if (!GetWindowRect(_window, out var bounds))
        {
            _log.Warning("The initial window bounds could not be read for minimum window sizing.");
            return;
        }
        var minimum = GetMinimum();
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width >= minimum.Width && height >= minimum.Height) return;
        if (!SetWindowPos(_window, 0, 0, 0, Math.Max(width, minimum.Width), Math.Max(height, minimum.Height), KeepPositionAndActivation))
            _log.Warning("The minimum window bounds could not be applied.");
    }

    public void Dispose()
    {
        if (_window == 0) return;
        if (!RemoveWindowSubclass(_window, _procedure, SubclassId))
        {
            // Keep the delegate rooted until WM_NCDESTROY if native removal failed.
            _log.Warning("The minimum window size handler could not be removed before window destruction.");
            return;
        }
        ReleaseLifetime();
    }

    private void ReleaseLifetime()
    {
        _window = 0;
        if (_lifetime.IsAllocated) _lifetime.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
}
