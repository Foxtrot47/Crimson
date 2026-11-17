using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Resources;

namespace Crimson.Providers.Ubisoft;

/// <summary>One registry value; <see cref="Key"/> is the full path including the value name.</summary>
public sealed record UbisoftRegistryWrite(string Key, string? StringValue, uint? NumberValue, bool BothViews = true);

public sealed record UbisoftSystemRun(string Executable, string WorkingDirectory, string Arguments, string Description,
    bool IgnoreExitCode);

/// <summary>
/// <see cref="DeleteKeys"/> entries ending in a backslash remove a key tree; others remove one value.
/// <see cref="LauncherInstallDir"/> (a Crimson runtime folder) is written to <c>SOFTWARE\Ubisoft\Launcher\InstallDir</c>
/// in the 32-bit view, the only one the game's runtime loaders read,
/// when a game is launched: Ubisoft Connect rewrites that value whenever it runs, so it is set right before it
/// is needed rather than at install time. <see cref="RemoveLauncherInstallDir"/> deletes it again, only while it
/// still points at a Crimson runtime folder; it is requested when the last Ubisoft game is uninstalled.
/// </summary>
public sealed record UbisoftSystemSetupRequest(
    IReadOnlyList<UbisoftRegistryWrite> Writes,
    IReadOnlyList<string> DeleteKeys,
    IReadOnlyList<UbisoftSystemRun> Runs,
    string? LauncherInstallDir,
    bool RemoveLauncherInstallDir = false)
{
    public bool IsEmpty => Writes.Count == 0 && DeleteKeys.Count == 0 && Runs.Count == 0 && LauncherInstallDir is null &&
        !RemoveLauncherInstallDir;

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        if (Writes.Count > 0) lines.Add(UiStrings.UbisoftElevationRegistry);
        if (Runs.Count > 0)
            lines.Add(UiStrings.Format(UiStrings.UbisoftElevationPrerequisitesFormat,
                string.Join(", ", Runs.Select(run => run.Description).Distinct(StringComparer.OrdinalIgnoreCase))));
        if (LauncherInstallDir is not null) lines.Add(UiStrings.UbisoftElevationLauncherFolder);
        if (DeleteKeys.Count > 0) lines.Add(UiStrings.UbisoftElevationRemoveRegistry);
        if (RemoveLauncherInstallDir) lines.Add(UiStrings.UbisoftElevationRemoveLauncherFolder);
        return lines;
    }
}

/// <summary><see cref="ExitCode"/> is <see cref="NotStarted"/> when the program could not be started or timed out.</summary>
public sealed record UbisoftSystemRunResult(string Description, int ExitCode)
{
    public const int NotStarted = int.MinValue;
}

/// <summary><see cref="Applied"/> is false when the prompt was declined or the helper failed.</summary>
public sealed record UbisoftSystemSetupResult(bool Applied, IReadOnlyList<UbisoftSystemRunResult> Runs, string? Error);

public interface IUbisoftSystemSetup
{
    Task<UbisoftSystemSetupResult> ApplyAsync(UbisoftSystemSetupRequest request, CancellationToken ct);
}

/// <summary>
/// What the elevated helper accepts. Only Ubisoft keys under HKLM may be touched, the real client's own
/// launcher keys are off limits, and only programs inside an installation this request registers may run.
/// </summary>
public static class UbisoftSystemSetupPolicy
{
    public const string MachineHive = @"HKEY_LOCAL_MACHINE\";

    /// <summary>Command-line switch of the elevated helper: <c>--ubisoft-system-setup &lt;request.json&gt; &lt;result.json&gt;</c>.</summary>
    public const string HelperArgument = "--ubisoft-system-setup";

    public const string CrimsonRuntimeFolder = @"Assets\UbisoftRuntime";

    private static readonly string[] Roots = [@"SOFTWARE\Ubisoft\", @"SOFTWARE\WOW6432Node\Ubisoft\"];
    private static readonly string[] RunnableExtensions = [".exe", ".bat", ".cmd"];

    public static IReadOnlyList<string> Validate(UbisoftSystemSetupRequest? request)
    {
        var errors = new List<string>();
        if (request is not { Writes: not null, DeleteKeys: not null, Runs: not null })
        {
            errors.Add("The request is incomplete.");
            return errors;
        }

        foreach (var write in request.Writes)
            if (WriteError(write) is { } error) errors.Add(error);
        foreach (var key in request.DeleteKeys)
            if (DeleteError(key) is { } error) errors.Add(error);
        var installRoots = InstallRoots(request.Writes);
        foreach (var run in request.Runs) AddRunErrors(errors, run, installRoots);
        if (request.LauncherInstallDir is { } launcher && !IsAcceptedLauncherDirectory(launcher))
            errors.Add("The launcher folder is not a Crimson runtime folder.");
        return errors;
    }

    private static string? WriteError(UbisoftRegistryWrite? write)
    {
        if (write is null || !TrySplit(write.Key, out _, out var value, out var relative) || value is null)
            return $"Registry value '{write?.Key}' is outside the Ubisoft keys.";
        if (IsLauncherOwned(relative)) return $"Registry value '{write.Key}' belongs to the Ubisoft Connect launcher.";
        if ((write.StringValue is null) == (write.NumberValue is null))
            return $"Registry value '{write.Key}' needs exactly one string or number.";
        if (write.StringValue?.IndexOf('\0') >= 0) return $"Registry value '{write.Key}' contains a NUL character.";
        return null;
    }

    private static string? DeleteError(string? key)
    {
        if (key is null || !TrySplit(key, out _, out _, out var relative))
            return $"Registry key '{key}' is outside the Ubisoft keys.";
        return IsLauncherOwned(relative) ? $"Registry key '{key}' belongs to the Ubisoft Connect launcher." : null;
    }

    private static void AddRunErrors(List<string> errors, UbisoftSystemRun? run, List<string> installRoots)
    {
        if (run is null || !IsAbsolutePath(run.Executable) || !IsAbsolutePath(run.WorkingDirectory))
        {
            errors.Add($"Program '{run?.Description}' has no absolute path.");
            return;
        }
        if (!RunnableExtensions.Contains(Path.GetExtension(run.Executable), StringComparer.OrdinalIgnoreCase))
            errors.Add($"Program '{run.Description}' is not an executable.");
        else if (!IsInsideInstallation(run, installRoots))
            errors.Add($"Program '{run.Description}' is outside the game's installation folder.");
        if ((run.Arguments ?? "").IndexOf('\0') >= 0) errors.Add($"Program '{run.Description}' has invalid arguments.");
    }

    private static bool IsAbsolutePath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool IsInsideInstallation(UbisoftSystemRun run, List<string> installRoots) =>
        installRoots.Any(root => Contains(root, run.Executable)) &&
        installRoots.Any(root => Contains(root, run.WorkingDirectory, allowRoot: true));

    private static bool IsAcceptedLauncherDirectory(string launcher) =>
        Path.IsPathFullyQualified(launcher) && IsCrimsonRuntimeDirectory(launcher);

    /// <summary>
    /// Splits <c>HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft\A\B</c> into the sub key (<c>SOFTWARE\Ubisoft\A</c>) and value
    /// name (<c>B</c>); a trailing backslash names the key itself (value null). False for anything else.
    /// </summary>
    public static bool TrySplit(string path, out string subKey, out string? valueName) =>
        TrySplit(path, out subKey, out valueName, out _);

    public static bool IsCrimsonRuntimeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));
        return trimmed.EndsWith("\\" + CrimsonRuntimeFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> InstallRoots(IEnumerable<UbisoftRegistryWrite> writes) =>
        writes.Select(InstallRoot).OfType<string>().ToList();

    /// <summary>
    /// The installation folder a <c>Launcher\Installs\&lt;id&gt;\InstallDir</c> write registers; null for any other
    /// write. A request's programs may only run from these folders, so the write is kept whenever programs run.
    /// </summary>
    public static string? InstallRoot(UbisoftRegistryWrite? write)
    {
        if (write?.StringValue is not { Length: > 0 } value || !TrySplit(write.Key, out _, out var name, out var relative) ||
            !string.Equals(name, "InstallDir", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = relative.Split('\\');
        if (segments.Length != 3 || !IsInstallsRecord(segments)) return null;
        var path = value.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // A drive root cannot be an installation: everything on the drive would become runnable.
        return IsDriveRoot(full) ? null : full;
    }

    private static bool IsDriveRoot(string full) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string root, string path, bool allowRoot = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (allowRoot && string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplit(string path, out string subKey, out string? valueName, out string relative)
    {
        subKey = "";
        valueName = null;
        relative = "";
        if (UbisoftRoot(path) is not { } root) return false;
        var rest = path[MachineHive.Length..];
        var tree = rest.EndsWith('\\');
        relative = tree ? rest[root.Length..^1] : rest[root.Length..];
        var segments = relative.Split('\\');
        if (segments.Any(segment => segment.Length == 0 || segment.Trim() is "." or "..")) return false;
        if (tree)
        {
            subKey = rest[..^1];
            return segments.Length >= 1;
        }
        // A value needs a key below Ubisoft: values directly on the Ubisoft key are not ours to touch.
        if (segments.Length < 2) return false;
        var lastSlash = rest.LastIndexOf('\\');
        subKey = rest[..lastSlash];
        valueName = rest[(lastSlash + 1)..];
        relative = relative[..relative.LastIndexOf('\\')];
        return true;
    }

    private static string? UbisoftRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(MachineHive, StringComparison.OrdinalIgnoreCase) ||
            path.Any(character => character < ' ' || character == '/')) return null;
        var rest = path[MachineHive.Length..];
        return Roots.FirstOrDefault(candidate => rest.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The launcher key itself, and anything under it other than one product's <c>Installs\&lt;id&gt;</c> record,
    /// belongs to Ubisoft Connect (its InstallDir goes only through <see cref="UbisoftSystemSetupRequest.LauncherInstallDir"/>
    /// and <see cref="UbisoftSystemSetupRequest.RemoveLauncherInstallDir"/>).
    /// </summary>
    private static bool IsLauncherOwned(string relativeKey)
    {
        var segments = relativeKey.Split('\\');
        return segments[0].Equals("Launcher", StringComparison.OrdinalIgnoreCase) && !IsInstallsRecord(segments);
    }

    private static bool IsInstallsRecord(string[] segments) =>
        segments.Length >= 3 && segments[0].Equals("Launcher", StringComparison.OrdinalIgnoreCase) &&
        segments[1].Equals("Installs", StringComparison.OrdinalIgnoreCase) && IsProductId(segments[2]);

    private static bool IsProductId(string segment) =>
        uint.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
}

internal static class UbisoftSystemSetupRequests
{
    private const string InstallDirToken = "$INSTALL_DIR";

    internal static string RunKey(UbisoftInstallRun run) =>
        (string.IsNullOrWhiteSpace(run.Identifier) ? run.Exe : run.Identifier) + ":" +
        run.Version.ToString(CultureInfo.InvariantCulture);

    internal static string LauncherInstallsKey(uint productId) =>
        UbisoftSystemSetupPolicy.MachineHive + @"SOFTWARE\Ubisoft\Launcher\Installs\" +
        productId.ToString(CultureInfo.InvariantCulture) + "\\";

    /// <summary>
    /// Registry values for an installation (manifest values with <c>$INSTALL_DIR</c> substituted, both views,
    /// the selected language's entry; the launcher's per-product record in the 32-bit view) and, when
    /// <paramref name="includeRuns"/> is set, every applicable install run not yet completed.
    /// </summary>
    internal static (UbisoftSystemSetupRequest Request, IReadOnlyList<string> RunKeys) ForInstall(uint productId,
        string installPath, UbisoftManifest? manifest, IReadOnlyList<string> languages,
        IReadOnlyCollection<string> completedRuns, bool includeRuns)
    {
        var language = languages.FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        var writes = ManifestWrites(manifest, installPath, language);
        var launcher = LauncherInstallsKey(productId);
        writes.Add(new UbisoftRegistryWrite(launcher + "InstallDir", ForwardSlashes(installPath), null, BothViews: false));
        if (language is not null) writes.Add(new UbisoftRegistryWrite(launcher + "Language", language, null, BothViews: false));

        List<UbisoftInstallRun> pending = includeRuns && manifest is not null ? PendingRuns(manifest, completedRuns) : [];
        var runs = pending.Select(run => SystemRun(run, installPath)).ToList();
        return (new UbisoftSystemSetupRequest(writes, [], runs, null), pending.Select(RunKey).ToList());
    }

    private static List<UbisoftRegistryWrite> ManifestWrites(UbisoftManifest? manifest, string installPath, string? language)
    {
        var writes = new List<UbisoftRegistryWrite>();
        foreach (var entry in manifest?.InstallRegistry ?? [])
        {
            if (ValueKey(entry.Key) is not { } key || PickValue(entry.Values, language) is not { } value) continue;
            if (RegistryWrite(key, entry.IsNumber, Substitute(value.Value, installPath)) is { } write) writes.Add(write);
        }
        return writes;
    }

    private static UbisoftRegistryWrite? RegistryWrite(string key, bool isNumber, string text)
    {
        if (!isNumber) return new UbisoftRegistryWrite(key, text, null);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? new UbisoftRegistryWrite(key, null, number)
            : null;
    }

    private static List<UbisoftInstallRun> PendingRuns(UbisoftManifest manifest, IReadOnlyCollection<string> completedRuns)
    {
        var seen = completedRuns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new List<UbisoftInstallRun>();
        foreach (var run in manifest.InstallRuns)
            if (AppliesToThisMachine(run) && seen.Add(RunKey(run))) pending.Add(run);
        return pending;
    }

    private static UbisoftSystemRun SystemRun(UbisoftInstallRun run, string installPath)
    {
        var executable = Resolve(Substitute(run.Exe, installPath), installPath);
        var workingDirectory = string.IsNullOrWhiteSpace(run.WorkingDirectory)
            ? Path.GetDirectoryName(executable) ?? installPath
            : Resolve(Substitute(run.WorkingDirectory, installPath), installPath);
        return new UbisoftSystemRun(executable, workingDirectory, Substitute(run.Arguments ?? "", installPath),
            string.IsNullOrWhiteSpace(run.Description) ? Path.GetFileName(executable) : run.Description,
            run.IgnoreAllExitCodes);
    }

    /// <summary>
    /// Removal of an installation's registry footprint. The game's own keys are only removed when
    /// <paramref name="includeGameKeys"/> is set: another installation may still own them. The launcher's
    /// InstallDir goes with the last Ubisoft game Crimson manages (<paramref name="lastGame"/>).
    /// </summary>
    internal static UbisoftSystemSetupRequest ForRemoval(uint productId, UbisoftManifest? manifest, bool includeGameKeys,
        bool includeLauncherRecord, bool lastGame)
    {
        var deletes = new List<string>();
        if (includeGameKeys && manifest is not null) deletes.AddRange(GameKeys(manifest));
        if (includeLauncherRecord) deletes.Add(LauncherInstallsKey(productId));
        return new UbisoftSystemSetupRequest([], deletes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), [], null,
            RemoveLauncherInstallDir: lastGame);
    }

    private static IEnumerable<string> GameKeys(UbisoftManifest manifest)
    {
        foreach (var entry in manifest.InstallRegistry)
            if (ValueKey(entry.Key) is { } key) yield return key;
        foreach (var raw in manifest.UninstallRegistry)
        {
            if (NormalizeHive(raw) is not { } key) continue;
            // Uninstall entries name keys: remove the tree.
            var tree = key.EndsWith('\\') ? key : key + "\\";
            if (UbisoftSystemSetupPolicy.TrySplit(tree, out _, out _)) yield return tree;
        }
    }

    private static string? ValueKey(string? raw) =>
        NormalizeHive(raw) is { } key && UbisoftSystemSetupPolicy.TrySplit(key, out _, out var name) && name is not null
            ? key
            : null;

    internal static string ForwardSlashes(string installPath) =>
        Path.TrimEndingDirectorySeparator(installPath).Replace('\\', '/') + "/";

    /// <summary>Exit codes that mean the prerequisite is in place (success, or success pending a restart).
    /// A program that never ran is a failure even when its exit code is ignored.</summary>
    internal static bool Succeeded(UbisoftSystemRun run, int exitCode) =>
        exitCode != UbisoftSystemRunResult.NotStarted && (run.IgnoreExitCode || exitCode is 0 or 3010 or 1641);

    private static UbisoftRegistryValue? PickValue(IReadOnlyList<UbisoftRegistryValue> values, string? language)
    {
        if (values.Count == 0) return null;
        if (language is not null &&
            values.FirstOrDefault(value => string.Equals(value.Language, language, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        if (values.FirstOrDefault(value => string.IsNullOrEmpty(value.Language)) is { } neutral) return neutral;
        return values.FirstOrDefault(value => string.Equals(value.Language, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? values[0];
    }

    private static string? NormalizeHive(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim().Replace('/', '\\');
        if (key.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            key = UbisoftSystemSetupPolicy.MachineHive + key[5..];
        return key.StartsWith(UbisoftSystemSetupPolicy.MachineHive, StringComparison.OrdinalIgnoreCase)
            ? UbisoftSystemSetupPolicy.MachineHive + key[UbisoftSystemSetupPolicy.MachineHive.Length..]
            : null;
    }

    private static string Substitute(string text, string installPath) =>
        text.Replace(InstallDirToken, Path.TrimEndingDirectorySeparator(installPath), StringComparison.OrdinalIgnoreCase);

    private static string Resolve(string path, string installPath)
    {
        path = path.Replace('/', '\\');
        return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(installPath, path.TrimStart('\\')));
    }

    /// <summary>Runs pinned to another Windows release (e.g. a Windows 7 DWM fix) or a 64-bit OS are skipped.</summary>
    private static bool AppliesToThisMachine(UbisoftInstallRun run)
    {
        if (run.PlatformType == 2 && !Environment.Is64BitOperatingSystem) return false;
        return run.Platform is null || run.Platform == CurrentPlatform();
    }

    private static uint CurrentPlatform()
    {
        var version = Environment.OSVersion.Version;
        return version.Major switch
        {
            >= 10 => 5,
            6 when version.Minor >= 3 => 6,
            6 when version.Minor == 2 => 4,
            6 when version.Minor == 1 => 3,
            6 => 2,
            _ => 1
        };
    }
}
