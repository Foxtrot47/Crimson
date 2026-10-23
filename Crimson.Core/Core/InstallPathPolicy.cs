using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Crimson.Core;

internal static class InstallPathPolicy
{
    public static string GetDirectoryName(string title)
    {
        // Use Windows rules even when the portable core runs on another OS.
        var name = string.Concat(title.Select(character =>
            character < ' ' || "<>:\"/\\|?*".Contains(character) ? '_' : character)).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return "Game";
        var reserved = Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]) *(\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return reserved ? "_" + name : name;
    }

    public static void RemoveEmptyDirectories(string installRoot, IEnumerable<string> preservedRoots)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (!Directory.Exists(root) || IsReparsePoint(root) || Directory.GetParent(root) is null) return;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var preserved = preservedRoots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToHashSet(comparer);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var directories = Directory.EnumerateDirectories(root, "*", options)
            .OrderByDescending(path => path.Length).Append(root);
        foreach (var directory in directories)
        {
            if (preserved.Contains(directory)) continue;
            RejectLinkedDescendants(root, directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    public static void DeleteFile(string installRoot, string filePath)
    {
        var file = RequireFile(installRoot, filePath);
        try
        {
            File.Delete(file);
        }
        catch (DirectoryNotFoundException)
        {
            // A partial installation may never have created this manifest entry's parent folder.
        }
    }

    public static string ResolveFile(string installRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("A manifest file path must be relative to the installation folder.");

        return RequireFile(installRoot, Path.Combine(installRoot, normalized));
    }

    public static string RequireFile(string installRoot, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var file = Path.GetFullPath(filePath);
        RequireContainedFile(root, file);
        RejectLinkedDescendants(root, file);
        return file;
    }

    private static void RequireContainedFile(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        if (relative is "." or ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("A manifest file path escaped the installation folder.");
    }

    private static void RejectLinkedDescendants(string root, string file)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var path = file; !string.Equals(path, root, comparison); path = Path.GetDirectoryName(path)!)
        {
            if (IsReparsePoint(path))
                throw new InvalidDataException("Manifest files cannot traverse symbolic links or reparse points.");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
