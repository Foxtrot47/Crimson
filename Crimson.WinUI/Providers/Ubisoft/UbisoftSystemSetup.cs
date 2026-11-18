using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Crimson.Providers.Ubisoft;

/// <summary>A prompt the user never approves is a normal outcome (<c>Applied = false</c>), not an error.</summary>
public sealed class UbisoftSystemSetup(ElevationNotice notice, ILogger log) : IUbisoftSystemSetup
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private readonly ILogger _log = log.ForContext<UbisoftSystemSetup>();

    public async Task<UbisoftSystemSetupResult> ApplyAsync(UbisoftSystemSetupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsEmpty) return new UbisoftSystemSetupResult(true, [], null);
        // Refuse here too: a request the helper would reject must not cost the user a prompt.
        if (UbisoftSystemSetupPolicy.Validate(request) is { Count: > 0 } errors)
        {
            _log.Warning("Ubisoft: system setup request refused ({Count} problems)", errors.Count);
            return new UbisoftSystemSetupResult(false, [], string.Join(" ", errors));
        }
        var elevated = request with { Runs = [] };
        if (elevated.IsEmpty)
            return new UbisoftSystemSetupResult(true, await RunProgramsAsync(request.Runs, ct), null);
        if (Environment.ProcessPath is not { Length: > 0 } executable)
            return new UbisoftSystemSetupResult(false, [], "Crimson's executable path is unknown.");

        var pipeName = "Crimson.UbisoftSetup." + Guid.NewGuid().ToString("N");
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(executable)
        {
            Arguments = $"{UbisoftSystemSetupPolicy.HelperArgument} \"{pipeName}\" {Environment.ProcessId} \"{executable}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        var (process, failure) = await StartElevatedAsync(elevated, start, ct);
        if (process is null)
        {
            _log.Information("Ubisoft: system setup was not approved ({Failure})", failure);
            return new UbisoftSystemSetupResult(false, [], failure);
        }
        using (process)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                await client.ConnectAsync(deadline.Token);
                await SetupPipe.WriteAsync(client, JsonSerializer.Serialize(elevated), deadline.Token);
                var result = await SetupPipe.ReadAsync<UbisoftSystemSetupResult>(client, deadline.Token);
                if (result is null)
                    return new UbisoftSystemSetupResult(false, [], "The setup helper result is empty.");
                _log.Information("Ubisoft: registry setup {Outcome}", result.Applied ? "applied" : "failed");
                // Prerequisite installers are never elevated by Crimson. They run as the signed-in user and may
                // show their own prompts; this removes the arbitrary-elevated-program path from the helper.
                return result.Applied
                    ? result with { Runs = await RunProgramsAsync(request.Runs, ct) }
                    : result with { Runs = [] };
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or
                JsonException or InvalidDataException)
            {
                _log.Warning("Ubisoft: system setup helper did not answer ({Error})", error.GetType().Name);
                return new UbisoftSystemSetupResult(false, [], error.GetType().Name);
            }
        }
    }

    private async Task<(Process? Process, string Failure)> StartElevatedAsync(UbisoftSystemSetupRequest request,
        ProcessStartInfo start, CancellationToken ct)
    {
        Process? process = null;
        var failure = "declined";
        await notice.RunAsync(request.Describe(), () =>
        {
            try
            {
                process = Process.Start(start);
                if (process is not null) return ElevationOutcome.Approved;
                failure = "The setup helper did not start.";
            }
            catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled)
            {
                _log.Information("Ubisoft: the administrator prompt for system setup was declined");
                failure = "declined";
                return ElevationOutcome.Declined;
            }
            catch (Win32Exception error)
            {
                _log.Warning("Ubisoft: the administrator prompt could not be opened ({Code})", error.NativeErrorCode);
                failure = error.GetType().Name;
            }
            return ElevationOutcome.Failed;
        }, ct);
        return (process, failure);
    }

    /// <summary>Prerequisites run unelevated, sequentially, after the registry step. A program that self-elevates
    /// shows its own Windows prompt; Crimson never executes a game-supplied program as administrator.</summary>
    private async Task<IReadOnlyList<UbisoftSystemRunResult>> RunProgramsAsync(IReadOnlyList<UbisoftSystemRun> runs,
        CancellationToken ct)
    {
        var results = new List<UbisoftSystemRunResult>(runs.Count);
        foreach (var run in runs)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RunTimeout);
            try
            {
                var start = new ProcessStartInfo(run.Executable)
                {
                    Arguments = run.Arguments ?? "",
                    WorkingDirectory = run.WorkingDirectory,
                    // The shell honors the installer's own manifest. Crimson never attaches an elevated token; a
                    // prerequisite that needs administrator rights shows its own prompt naming that binary.
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                using var process = Process.Start(start);
                if (process is null)
                {
                    results.Add(NotStarted(run));
                    continue;
                }
                await process.WaitForExitAsync(timeout.Token);
                results.Add(new UbisoftSystemRunResult(run.Description, process.ExitCode));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results.Add(NotStarted(run));
                throw;
            }
            catch (Exception error) when (error is OperationCanceledException or Win32Exception or
                InvalidOperationException or IOException)
            {
                _log.Warning("Ubisoft: prerequisite {Description} did not finish ({Error})", run.Description,
                    error.GetType().Name);
                results.Add(NotStarted(run));
            }
        }
        return results;
    }

    private static UbisoftSystemRunResult NotStarted(UbisoftSystemRun run) =>
        new(run.Description, UbisoftSystemRunResult.NotStarted);
}

/// <summary>Entered from <c>Program.Main</c> before any app activation.</summary>
internal static class UbisoftElevatedSystemSetup
{
    private const string LauncherKey = @"SOFTWARE\Ubisoft\Launcher";

    internal static int Run(string pipeName, string parentPidText, string expectedPath)
    {
        if (!int.TryParse(parentPidText, out var parentPid) || parentPid <= 0) return 2;
        try
        {
            using var server = CreateSetupPipe(pipeName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            server.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
            if (!IsExpectedClient(server.SafePipeHandle, parentPid, expectedPath)) return 2;
            var request = SetupPipe.ReadAsync<UbisoftSystemSetupRequest>(server, deadline.Token).GetAwaiter().GetResult();
            var result = Apply(request);
            SetupPipe.WriteAsync(server, JsonSerializer.Serialize(result), deadline.Token).GetAwaiter().GetResult();
            return result.Applied ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException or OperationCanceledException)
        {
            return 3;
        }
    }

    /// <summary>
    /// UAC may elevate with a different administrator account, so the pipe cannot be CurrentUserOnly. The
    /// authenticated-user ACL lets the unelevated parent connect; PID and image checks decide whether to serve it.
    /// </summary>
    private static NamedPipeServerStream CreateSetupPipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    /// <summary>
    /// The client must be the exact Crimson process that started this helper. The pipe name is public in the
    /// command line, so PID and image-path checks stop another process from feeding the elevated helper.
    /// </summary>
    private static bool IsExpectedClient(SafePipeHandle pipe, int parentPid, string expectedPath)
    {
        if (!GetNamedPipeClientProcessId(pipe, out var clientPid) || clientPid != (uint)parentPid) return false;
        if (Environment.ProcessPath is not { Length: > 0 } helperPath ||
            !string.Equals(Path.GetFullPath(expectedPath), Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var process = Process.GetProcessById(parentPid);
            var image = process.MainModule?.FileName;
            return image is { Length: > 0 } &&
                string.Equals(Path.GetFullPath(image), Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    internal static UbisoftSystemSetupResult Apply(UbisoftSystemSetupRequest? request)
    {
        if (UbisoftSystemSetupPolicy.Validate(request) is { Count: > 0 } errors)
            return new UbisoftSystemSetupResult(false, [], string.Join(" ", errors));
        try
        {
            foreach (var path in request!.DeleteKeys) Delete(path);
            foreach (var write in request.Writes) Write(write);
            if (request.LauncherInstallDir is { } launcher) WriteLauncherDirectory(launcher);
            if (request.RemoveLauncherInstallDir) RemoveLauncherDirectory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException)
        {
            return new UbisoftSystemSetupResult(false, [], error.GetType().Name + ": " + error.Message);
        }
        // Programs are intentionally never executed here: the parent runs prerequisites unelevated.
        return new UbisoftSystemSetupResult(true, [], null);
    }

    private static readonly RegistryView[] BothViews = [RegistryView.Registry64, RegistryView.Registry32];

    private static void Delete(string path)
    {
        if (!UbisoftSystemSetupPolicy.TrySplit(path, out var subKey, out var valueName)) return;
        foreach (var view in BothViews)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            if (valueName is null)
            {
                root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                continue;
            }
            using var key = root.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static void Write(UbisoftRegistryWrite write)
    {
        if (!UbisoftSystemSetupPolicy.TrySplit(write.Key, out var subKey, out var valueName) || valueName is null) return;
        foreach (var view in write.BothViews ? BothViews : [RegistryView.Registry32])
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.CreateSubKey(subKey, writable: true);
            if (write.NumberValue is { } number) key.SetValue(valueName, unchecked((int)number), RegistryValueKind.DWord);
            else key.SetValue(valueName, write.StringValue!, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Every loader (uplay_r1_loader, upc_r2_loader; 32- and 64-bit) reads only the 32-bit view, so the 64-bit view,
    /// which the real client also writes, is left alone. Ubisoft Connect, when it runs, sets its own folder back.
    /// </summary>
    private static void WriteLauncherDirectory(string directory)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = root.CreateSubKey(LauncherKey, writable: true);
        key.SetValue("InstallDir", Path.TrimEndingDirectorySeparator(directory) + "\\", RegistryValueKind.String);
    }

    /// <summary>
    /// Only a value pointing at a Crimson runtime folder is removed: never a real client's. Both views are checked
    /// because earlier builds wrote both.
    /// </summary>
    private static void RemoveLauncherDirectory()
    {
        foreach (var view in BothViews)
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(LauncherKey, writable: true);
            if (key?.GetValue("InstallDir") is string current && UbisoftSystemSetupPolicy.IsCrimsonRuntimeDirectory(current))
                key.DeleteValue("InstallDir", throwOnMissingValue: false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

/// <summary>Length-prefixed JSON over the private setup pipe.</summary>
internal static class SetupPipe
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;

    internal static async Task WriteAsync(Stream stream, string value, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(value);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var body = await ReadBytesAsync(stream, ct);
        return JsonSerializer.Deserialize<T>(body);
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid setup pipe message.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }
}
