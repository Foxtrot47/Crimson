using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Crimson.Utils;

public static class InstallDirectoryCleanup
{
    public static void RemoveEmptyOwnedDirectories(
        string installRoot,
        IEnumerable<string> manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);

        var canonicalRoot = Path.GetFullPath(installRoot);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in manifestPaths)
        {
            var filePath = ManifestPath.ResolveUnderRoot(canonicalRoot, manifestPath);
            var directory = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(directory) &&
                   !string.Equals(directory, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                directories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        var tempDirectory = ManifestPath.ResolveUnderRoot(canonicalRoot, ".Crimson");
        if (Directory.Exists(tempDirectory))
        {
            tempDirectory = ManifestPath.RevalidateUnderRoot(canonicalRoot, tempDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
            TryDeleteIfEmpty(canonicalRoot, directory);

        TryDeleteIfEmpty(canonicalRoot, canonicalRoot);
    }

    private static void TryDeleteIfEmpty(string installRoot, string directory)
    {
        try
        {
            var path = ManifestPath.RevalidateUnderRoot(installRoot, directory);
            Directory.Delete(path, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // Preserve directories containing untracked user files.
        }
    }
}
