using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Crimson.Utils;

public static class ManifestPath
{
    public static string ResolveUnderRoot(string installRoot, string relativePath) =>
        ResolveUnderRoot(installRoot, ManifestRelativePath.Parse(relativePath));

    public static string ResolveUnderRoot(string installRoot, ManifestRelativePath relativePath)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("Install root is required.", nameof(installRoot));
        ArgumentNullException.ThrowIfNull(relativePath);

        try
        {
            var canonicalRoot = Path.GetFullPath(installRoot);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.ToPlatformPath()));
            return ValidateUnderRoot(canonicalRoot, candidate, relativePath.Value);
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

    public static string? ResolveExistingImportFile(
        string installRoot,
        ManifestRelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var canonicalRoot = RevalidateUnderRoot(installRoot, installRoot);
        if (!Directory.Exists(canonicalRoot))
            return null;

        var currentPath = canonicalRoot;
        foreach (var segment in relativePath.Segments)
        {
            if (!Directory.Exists(currentPath))
                return null;

            var matches = Directory.EnumerateFileSystemEntries(currentPath)
                .Where(entry => string.Equals(
                    Path.GetFileName(entry).Normalize(NormalizationForm.FormC),
                    segment,
                    StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
                return null;
            if (matches.Length > 1)
            {
                throw new InvalidDataException(
                    $"Imported path is ambiguous at segment '{segment}': {relativePath}");
            }

            currentPath = RevalidateUnderRoot(canonicalRoot, matches[0]);
        }

        return File.Exists(currentPath) ? currentPath : null;
    }

    public static string RevalidateUnderRoot(string installRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("Install root is required.", nameof(installRoot));
        if (string.IsNullOrWhiteSpace(candidatePath))
            throw new ArgumentException("Candidate path is required.", nameof(candidatePath));

        try
        {
            var canonicalRoot = Path.GetFullPath(installRoot);
            var candidate = Path.GetFullPath(candidatePath);
            return ValidateUnderRoot(canonicalRoot, candidate, candidatePath);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Mutation path is invalid: {candidatePath}", ex);
        }
    }

    public static IReadOnlyList<ManifestRelativePath> ValidateManifest(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ValidateManifest(paths.Select(ManifestRelativePath.Parse));
    }

    public static IReadOnlyList<ManifestRelativePath> ValidateManifest(
        IEnumerable<ManifestRelativePath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var logicalPaths = paths.ToList();
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

    private static string ValidateUnderRoot(
        string canonicalRoot,
        string candidate,
        string displayPath)
    {
        var pathFromRoot = Path.GetRelativePath(canonicalRoot, candidate);
        if (pathFromRoot == ".." ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(pathFromRoot))
        {
            throw new InvalidDataException($"Manifest path escapes the install root: {displayPath}");
        }

        RejectExistingReparsePoints(candidate, displayPath);
        return candidate;
    }

    private static void RejectExistingReparsePoints(string candidate, string displayPath)
    {
        var pathRoot = Path.GetPathRoot(candidate)
            ?? throw new InvalidDataException($"Manifest path has no filesystem root: {displayPath}");
        var currentPath = pathRoot;
        var pathFromRoot = Path.GetRelativePath(pathRoot, candidate);
        var segments = pathFromRoot.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Manifest path crosses a reparse point: {displayPath}");
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
