using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Crimson.Core;

/// <summary>
/// Shell-link access through source-generated COM interop, which stays correct when the app is trimmed;
/// classic <c>[ComImport]</c> interop depends on runtime-built marshalling that trimming does not support.
/// </summary>
internal sealed partial class ShellLinkInterop : IDisposable
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private const uint InprocServerContext = 0x1;
    private const uint RawPath = 0x4; // SLGP_RAWPATH
    private const int MaximumLength = 32768;

    private readonly ComObject _instance;
    private readonly IShellLinkW _link;
    private readonly IPersistFile _persist;

    private ShellLinkInterop(object instance)
    {
        _instance = (ComObject)instance;
        _link = (IShellLinkW)instance;
        _persist = (IPersistFile)instance;
    }

    internal static ShellLinkInterop Create()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClassId, IntPtr.Zero, InprocServerContext, typeof(IShellLinkW).GUID, out var pointer));
        try
        {
            return new ShellLinkInterop(ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance));
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal void Load(string path) => _persist.Load(path, 0);

    internal void Save(string path) => _persist.Save(path, true);

    internal unsafe string GetPath()
    {
        var buffer = new char[MaximumLength];
        fixed (char* target = buffer)
        {
            _link.GetPath(target, buffer.Length, IntPtr.Zero, RawPath);
            return new string(target);
        }
    }

    internal unsafe string GetArguments()
    {
        var buffer = new char[MaximumLength];
        fixed (char* arguments = buffer)
        {
            _link.GetArguments(arguments, buffer.Length);
            return new string(arguments);
        }
    }

    internal void SetPath(string path) => _link.SetPath(path);

    internal void SetArguments(string arguments) => _link.SetArguments(arguments);

    internal void SetDescription(string description) => _link.SetDescription(description);

    internal void SetIconLocation(string iconPath, int iconIndex) => _link.SetIconLocation(iconPath, iconIndex);

    public void Dispose() => _instance.FinalRelease();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId,
        out IntPtr instance);

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal unsafe partial interface IShellLinkW
    {
        void GetPath(char* file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(char* name, int maxName);
        void SetDescription(string name);
        void GetWorkingDirectory(char* directory, int maxPath);
        void SetWorkingDirectory(string directory);
        void GetArguments(char* arguments, int maxPath);
        void SetArguments(string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(char* iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation(string iconPath, int iconIndex);
        void SetRelativePath(string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string path);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load(string fileName, uint mode);
        void Save(string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string fileName);
        void GetCurFile(out IntPtr fileName);
    }
}
