using System;
using System.Collections.Generic;
using System.IO;

namespace Crimson.Utils;

public static class ManifestPath
{
    public static string ResolveUnderRoot(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("Install root is required.", nameof(installRoot));

        try
        {
            var logicalPath = ManifestRelativePath.Parse(relativePath);
            var canonicalRoot = Path.GetFullPath(installRoot);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, logicalPath.ToPlatformPath()));
            var pathFromRoot = Path.GetRelativePath(canonicalRoot, candidate);

            if (pathFromRoot == "." ||
                pathFromRoot == ".." ||
                pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(pathFromRoot))
            {
                throw new InvalidDataException($"Manifest path escapes the install root: {relativePath}");
            }

            RejectExistingReparsePoints(canonicalRoot, pathFromRoot, relativePath);
            return candidate;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Manifest path is invalid: {relativePath}", ex);
        }
    }

    public static IReadOnlyList<ManifestRelativePath> ValidateManifest(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var logicalPaths = paths.Select(ManifestRelativePath.Parse).ToList();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in logicalPaths)
        {
            if (!files.Add(path.Value))
                throw new InvalidDataException($"Duplicate or colliding manifest path: {path.Value}");
        }

        foreach (var path in logicalPaths)
        {
            var prefix = string.Empty;
            for (var index = 0; index < path.Segments.Count - 1; index++)
            {
                prefix = prefix.Length == 0
                    ? path.Segments[index]
                    : $"{prefix}/{path.Segments[index]}";
                if (files.Contains(prefix))
                    throw new InvalidDataException($"Manifest path collides with a parent file: {path.Value}");
            }
        }

        return logicalPaths;
    }

    private static void RejectExistingReparsePoints(string canonicalRoot, string pathFromRoot, string relativePath)
    {
        var currentPath = canonicalRoot;
        var segments = pathFromRoot.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Manifest path crosses a reparse point: {relativePath}");
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }
}
