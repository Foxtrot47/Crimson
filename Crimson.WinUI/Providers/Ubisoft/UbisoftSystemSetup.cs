using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Microsoft.Win32;
using Serilog;

namespace Crimson.Providers.Ubisoft;

/// <summary>A prompt the user never approves is a normal outcome (<c>Applied = false</c>), not an error.</summary>
public sealed class UbisoftSystemSetup(ElevationNotice notice, ILogger log) : IUbisoftSystemSetup
{
    private const int ErrorCancelled = 1223;
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
        if (Environment.ProcessPath is not { Length: > 0 } executable)
            return new UbisoftSystemSetupResult(false, [], "Crimson's executable path is unknown.");

        var folder = Path.Combine(Path.GetTempPath(), "Crimson", "ubisoft-setup-" + Guid.NewGuid().ToString("N"));
        var requestFile = Path.Combine(folder, "request.json");
        var resultFile = Path.Combine(folder, "result.json");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request), ct);
            // ShellExecute takes one argument string; the temporary paths contain no quotes.
            var start = new ProcessStartInfo(executable)
            {
                Arguments = $"{UbisoftSystemSetupPolicy.HelperArgument} \"{requestFile}\" \"{resultFile}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            var (process, failure) = await StartElevatedAsync(request, start, ct);
            if (process is null)
            {
                _log.Information("Ubisoft: system setup was not approved ({Failure})", failure);
                return new UbisoftSystemSetupResult(false, [], failure);
            }
            using (process)
            {
                // An elevated process cannot be stopped from here; cancellation only stops waiting for it.
                await process.WaitForExitAsync(ct);
            }
            return await ReadResultAsync(resultFile, ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            _log.Warning("Ubisoft: system setup helper failed ({Error})", error.GetType().Name);
            return new UbisoftSystemSetupResult(false, [], error.GetType().Name);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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

    private async Task<UbisoftSystemSetupResult> ReadResultAsync(string resultFile, CancellationToken ct)
    {
        if (!File.Exists(resultFile)) return new UbisoftSystemSetupResult(false, [], "The setup helper wrote no result.");
        var result = JsonSerializer.Deserialize<UbisoftSystemSetupResult>(await File.ReadAllTextAsync(resultFile, ct));
        if (result is null) return new UbisoftSystemSetupResult(false, [], "The setup helper result is empty.");
        _log.Information("Ubisoft: system setup {Outcome} ({Runs} programs)", result.Applied ? "applied" : "failed",
            result.Runs?.Count ?? 0);
        return result with { Runs = result.Runs ?? [] };
    }
}

/// <summary>Entered from <c>Program.Main</c> before any app activation.</summary>
internal static class UbisoftElevatedSystemSetup
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private const string LauncherKey = @"SOFTWARE\Ubisoft\Launcher";

    internal static int Run(string requestFile, string resultFile)
    {
        UbisoftSystemSetupResult result;
        try { result = Apply(JsonSerializer.Deserialize<UbisoftSystemSetupRequest>(File.ReadAllText(requestFile))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException)
        {
            result = new UbisoftSystemSetupResult(false, [], error.GetType().Name + ": " + error.Message);
        }
        try
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 3;
        }
        return result.Applied ? 0 : 1;
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
        return new UbisoftSystemSetupResult(true, request.Runs.Select(RunProgram).ToList(), null);
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

    private static UbisoftSystemRunResult RunProgram(UbisoftSystemRun run)
    {
        try
        {
            var start = new ProcessStartInfo(run.Executable)
            {
                Arguments = run.Arguments ?? "",
                WorkingDirectory = run.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            using var process = Process.Start(start);
            if (process is null) return new UbisoftSystemRunResult(run.Description, UbisoftSystemRunResult.NotStarted);
            if (!process.WaitForExit(RunTimeout))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                return new UbisoftSystemRunResult(run.Description, UbisoftSystemRunResult.NotStarted);
            }
            return new UbisoftSystemRunResult(run.Description, process.ExitCode);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return new UbisoftSystemRunResult(run.Description, UbisoftSystemRunResult.NotStarted);
        }
    }
}
