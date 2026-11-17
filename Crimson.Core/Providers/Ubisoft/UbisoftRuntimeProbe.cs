using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Titles from before roughly 2013 use the original Ubisoft Game Launcher and ship no loader DLL. The search is
/// bounded so a huge or looping folder cannot stall a launch.
/// </summary>
public static class UbisoftRuntimeProbe
{
    private static readonly string[] LoaderPrefixes = ["uplay_r1_loader", "uplay_r2_loader", "upc_r1_loader", "upc_r2_loader"];
    internal const int MaximumDepth = 4;
    internal const int MaximumEntries = 20_000;

    public static bool UsesModernRuntime(string installDirectory) => FindLoaders(installDirectory).Count > 0;

    /// <summary>Nearest folders first.</summary>
    public static IReadOnlyList<string> FindLoaders(string installDirectory)
    {
        var found = new List<string>();
        if (!Directory.Exists(installDirectory)) return found;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((installDirectory, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if (++visited > MaximumEntries) return found;
                    if (entry is DirectoryInfo)
                    {
                        if (depth < MaximumDepth) pending.Enqueue((entry.FullName, depth + 1));
                        continue;
                    }
                    var name = entry.Name;
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        LoaderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        found.Add(entry.FullName);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
