using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Crimson.Core;
using Serilog;

namespace Crimson.Tests;

public sealed class WindowSizeConstraintsTests
{
    [Theory]
    [InlineData(0u, 1497, 801)]
    [InlineData(96u, 1497, 801)]
    [InlineData(120u, 1872, 1002)]
    [InlineData(137u, 2137, 1144)]
    [InlineData(144u, 2246, 1202)]
    [InlineData(192u, 2994, 1602)]
    public void MinimumUsesMeasuredWindowSizeAndRoundsUpForDpi(uint dpi, int width, int height)
    {
        Assert.Equal((width, height), WindowSizeConstraints.CalculateMinimum(dpi, 4000, 3000));
    }

    [Theory]
    [InlineData(96u, 1280, 720, 1280, 720)]
    [InlineData(144u, 1920, 1080, 1920, 1080)]
    [InlineData(192u, 3840, 1080, 2994, 1080)]
    [InlineData(96u, 0, 0, 1497, 801)]
    public void MinimumDoesNotExceedAvailableMonitorWorkArea(uint dpi, int workWidth, int workHeight, int width, int height)
    {
        Assert.Equal((width, height), WindowSizeConstraints.CalculateMinimum(dpi, workWidth, workHeight));
    }

    [Fact]
    public void NativeResizeLimitsAreEnforcedWithoutChangingMaximizeLimits()
    {
        using var window = new HiddenWindow();
        using var logger = new LoggerConfiguration().CreateLogger();
        var original = ReadLimits(window.Handle);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(MonitorFromWindow(window.Handle, 2), ref monitor));
        var expected = WindowSizeConstraints.CalculateMinimum(GetDpiForWindow(window.Handle),
            monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        using var constraints = new WindowSizeConstraints(window.Handle, logger);

        var constrained = ReadLimits(window.Handle);
        Assert.True(GetWindowRect(window.Handle, out var bounds));

        Assert.Equal(expected.Width, constrained.MinimumTrackSize.X);
        Assert.Equal(expected.Height, constrained.MinimumTrackSize.Y);
        Assert.True(bounds.Right - bounds.Left >= expected.Width);
        Assert.True(bounds.Bottom - bounds.Top >= expected.Height);
        Assert.Equal(original.MaximumSize, constrained.MaximumSize);
        Assert.Equal(original.MaximumPosition, constrained.MaximumPosition);
        Assert.Equal(original.MaximumTrackSize, constrained.MaximumTrackSize);
        constraints.Dispose();
        Assert.Equal(original.MinimumTrackSize, ReadLimits(window.Handle).MinimumTrackSize);
    }

    [Fact]
    public void WindowDestructionReleasesCallbackLifetimeAndAllowsRepeatedDisposal()
    {
        using var window = new HiddenWindow();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var constraints = new WindowSizeConstraints(window.Handle, logger);

        window.Dispose();
        constraints.Dispose();
        constraints.Dispose();

        var root = (GCHandle)typeof(WindowSizeConstraints)
            .GetField("_lifetime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(constraints)!;
        Assert.False(root.IsAllocated);
    }

    [Fact]
    public void NativeCallbackOwnerSurvivesCollectionUntilWindowDestruction()
    {
        using var window = new HiddenWindow();
        using var logger = new LoggerConfiguration().CreateLogger();
        var reference = AttachWithoutManagedOwner(window.Handle, logger);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(reference.IsAlive);
        Assert.True(ReadLimits(window.Handle).MinimumTrackSize.X > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachWithoutManagedOwner(nint window, ILogger logger) =>
        new(new WindowSizeConstraints(window, logger));

    private static MinMaxInfo ReadLimits(nint window)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
        try
        {
            Marshal.StructureToPtr(new MinMaxInfo(), memory, false);
            SendMessage(window, 0x0024, 0, memory);
            return Marshal.PtrToStructure<MinMaxInfo>(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class HiddenWindow : IDisposable
    {
        internal nint Handle { get; private set; } = CreateWindowEx(0, "STATIC", "Crimson window sizing test",
            0x00CF0000, 100, 100, 320, 240, 0, 0, 0, 0);

        internal HiddenWindow() => Assert.NotEqual(nint.Zero, Handle);

        public void Dispose()
        {
            if (Handle == 0) return;
            Assert.True(DestroyWindow(Handle));
            Handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
}
