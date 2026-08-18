using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.Core;

public sealed class ManifestUpdatePlan
{
    public required IReadOnlyList<FileManifest> ChangedFiles { get; init; }
    public required IReadOnlyList<FileManifest> AddedFiles { get; init; }
    public required IReadOnlyList<ManifestRelativePath> RemovedFiles { get; init; }
    public required int UnchangedFileCount { get; init; }
}

public static class ManifestUpdatePlanner
{
    public static ManifestUpdatePlan Create(
        IReadOnlyList<FileManifest> oldFiles,
        IReadOnlyList<FileManifest> newFiles)
    {
        ArgumentNullException.ThrowIfNull(oldFiles);
        ArgumentNullException.ThrowIfNull(newFiles);

        var remainingOldFiles = new Dictionary<string, (ManifestRelativePath Path, byte[] Hash)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var oldFile in oldFiles)
        {
            if (!remainingOldFiles.TryAdd(oldFile.Path.Value, (oldFile.Path, oldFile.ShaHash)))
                throw new InvalidDataException($"Duplicate manifest path: {oldFile.Path}");
        }
        var changedFiles = new List<FileManifest>();
        var addedFiles = new List<FileManifest>();
        var unchangedFileCount = 0;

        foreach (var newFile in newFiles)
        {
            if (!remainingOldFiles.Remove(newFile.Path.Value, out var oldFile))
            {
                addedFiles.Add(newFile);
                continue;
            }

            if (newFile.ShaHash.SequenceEqual(oldFile.Hash))
                unchangedFileCount++;
            else
                changedFiles.Add(newFile);
        }

        return new ManifestUpdatePlan
        {
            ChangedFiles = changedFiles,
            AddedFiles = addedFiles,
            RemovedFiles = remainingOldFiles.Values.Select(file => file.Path).ToList(),
            UnchangedFileCount = unchangedFileCount
        };
    }
}
