using System;
using System.Collections.Generic;
using System.IO;

namespace Crimson.Utils;

public static class ManifestPath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ResolveUnderRoot(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("Install root is required.", nameof(installRoot));

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("Manifest path is empty.");

        try
        {
            if (Path.IsPathRooted(relativePath))
                throw new InvalidDataException($"Manifest path must be relative: {relativePath}");

            ValidateSegments(relativePath);

            var canonicalRoot = Path.GetFullPath(installRoot);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
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

    private static void ValidateSegments(string relativePath)
    {
        var segments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.None);
        var invalidCharacters = Path.GetInvalidFileNameChars();

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw new InvalidDataException($"Manifest path contains an invalid segment: {relativePath}");

            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(invalidCharacters) >= 0)
                throw new InvalidDataException($"Manifest path contains an invalid filename: {relativePath}");

            var deviceName = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(deviceName))
                throw new InvalidDataException($"Manifest path uses a reserved device name: {relativePath}");
        }
    }

    private static void RejectExistingReparsePoints(string canonicalRoot, string pathFromRoot, string relativePath)
    {
        var currentPath = canonicalRoot;
        var segments = pathFromRoot.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
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
