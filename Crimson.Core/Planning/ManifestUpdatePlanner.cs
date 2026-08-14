using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crimson.Models;

namespace Crimson.Core;

public sealed class ManifestUpdatePlan
{
    public required IReadOnlyList<FileManifest> ChangedFiles { get; init; }
    public required IReadOnlyList<FileManifest> AddedFiles { get; init; }
    public required IReadOnlyList<string> RemovedFiles { get; init; }
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

        var remainingOldFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldFile in oldFiles)
        {
            if (!remainingOldFiles.TryAdd(oldFile.Filename, oldFile.ShaHash))
                throw new InvalidDataException($"Duplicate manifest path: {oldFile.Filename}");
        }
        var changedFiles = new List<FileManifest>();
        var addedFiles = new List<FileManifest>();
        var unchangedFileCount = 0;

        foreach (var newFile in newFiles)
        {
            if (!remainingOldFiles.Remove(newFile.Filename, out var oldHash))
            {
                addedFiles.Add(newFile);
                continue;
            }

            if (newFile.ShaHash.SequenceEqual(oldHash))
                unchangedFileCount++;
            else
                changedFiles.Add(newFile);
        }

        return new ManifestUpdatePlan
        {
            ChangedFiles = changedFiles,
            AddedFiles = addedFiles,
            RemovedFiles = remainingOldFiles.Keys.ToList(),
            UnchangedFileCount = unchangedFileCount
        };
    }
}
