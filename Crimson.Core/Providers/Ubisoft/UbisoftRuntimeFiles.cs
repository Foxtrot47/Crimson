using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Every copy is recorded in <c>&lt;install&gt;/.Crimson/ubisoft-runtime.json</c> so uninstalling removes exactly what
/// Crimson placed. Registry locations are written by the system setup step, never from here.
/// </summary>
public static class UbisoftRuntimeFiles
{
    private const string RecordName = "ubisoft-runtime.json";

    /// <summary>The R1 SDK (<c>uplay_r1_loader</c>) also needs its aux helper; the R2 SDK (<c>upc_r2_loader</c>) loads <c>uplay_r2*.dll</c>.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["uplay_r1.dll", "uplay_r164.dll", "uplay_aux_r1.dll", "uplay_aux_r164.dll", "uplay_r2.dll", "uplay_r264.dll"];

    public static string BundledDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "UbisoftRuntime");

    /// <summary>Files already present are left alone: a game's own runtime wins.</summary>
    /// <returns>Install-relative paths of the files copied by this call.</returns>
    public static IReadOnlyList<string> Provision(string installDirectory, IEnumerable<string> targetDirectories,
        string? bundledDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        var source = bundledDirectory ?? BundledDirectory;
        var copied = new List<string>();
        if (!Directory.Exists(installDirectory) || !Directory.Exists(source)) return copied;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        foreach (var directory in targetDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsInside(root, directory) || !Directory.Exists(directory)) continue;
            foreach (var name in Names)
            {
                var from = Path.Combine(source, name);
                var target = Path.Combine(directory, name);
                if (!File.Exists(from) || File.Exists(target)) continue;
                try
                {
                    File.Copy(from, target, overwrite: false);
                    copied.Add(Path.GetRelativePath(root, target));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A read-only folder is not fatal: the loader also finds the runtime through the launcher key.
                }
            }
        }
        if (copied.Count > 0) WriteRecord(root, ReadRecord(root).Union(copied, StringComparer.OrdinalIgnoreCase).ToList());
        return copied;
    }

    /// <summary>
    /// Also removes unrecorded copies an older build left in the installation root. A file whose bytes no longer
    /// match the bundled copy belongs to the game and stays.
    /// </summary>
    public static int RemoveProvisioned(string? directory, string? bundledDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var source = bundledDirectory ?? BundledDirectory;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var recorded = ReadRecord(root);
        var removed = 0;
        foreach (var relative in recorded.Concat(Names).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsInside(root, path) || !File.Exists(path) || !MatchesBundled(path, Path.Combine(source, Path.GetFileName(path))))
                continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { File.Delete(RecordPath(root)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static string RecordPath(string root) => Path.Combine(root, ".Crimson", RecordName);

    private static List<string> ReadRecord(string root)
    {
        try
        {
            var file = RecordPath(root);
            if (!File.Exists(file)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file))?
                .Where(path => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)).ToList() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void WriteRecord(string root, List<string> paths)
    {
        try
        {
            var file = RecordPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(paths));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsInside(string root, string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesBundled(string path, string bundled)
    {
        if (!File.Exists(bundled)) return false;
        try
        {
            return new FileInfo(path).Length == new FileInfo(bundled).Length &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
