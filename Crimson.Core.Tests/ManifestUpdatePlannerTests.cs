using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.Tests;

public sealed class ManifestUpdatePlannerTests
{
    [Fact]
    public void Create_CategorizesUnchangedChangedAddedAndRemovedFiles()
    {
        var unchangedOld = File("Data/unchanged.bin", 1);
        var changedOld = File("Data/changed.bin", 2);
        var removed = File("Data/removed.bin", 3);
        var unchangedNew = File("Data/unchanged.bin", 1);
        var changedNew = File("Data/changed.bin", 4);
        var added = File("Data/added.bin", 5);

        var plan = ManifestUpdatePlanner.Create(
            [unchangedOld, changedOld, removed],
            [unchangedNew, changedNew, added]);

        Assert.Equal(1, plan.UnchangedFileCount);
        Assert.Equal([changedNew], plan.ChangedFiles);
        Assert.Equal([added], plan.AddedFiles);
        Assert.Equal(["Data/removed.bin"], plan.RemovedFiles.Select(path => path.Value));
    }

    [Fact]
    public void Create_MatchesWindowsPathsCaseInsensitively()
    {
        var oldFile = File("Data/Config.json", 1);
        var newFile = File("data/config.JSON", 1);

        var plan = ManifestUpdatePlanner.Create([oldFile], [newFile]);

        Assert.Equal(1, plan.UnchangedFileCount);
        Assert.Empty(plan.ChangedFiles);
        Assert.Empty(plan.AddedFiles);
        Assert.Empty(plan.RemovedFiles);
    }

    [Fact]
    public void Create_RejectsCaseInsensitiveDuplicateOldPaths()
    {
        var first = File("Data/config.json", 1);
        var duplicate = File("data/CONFIG.json", 2);

        var error = Assert.Throws<InvalidDataException>(() =>
            ManifestUpdatePlanner.Create([first, duplicate], []));

        Assert.Contains("data/CONFIG.json", error.Message);
    }

    [Fact]
    public void Create_DoesNotTreatSameLengthHashesAsEqual()
    {
        var oldFile = File("Data/file.bin", 1, 2, 3, 4);
        var newFile = File("Data/file.bin", 1, 2, 3, 5);

        var plan = ManifestUpdatePlanner.Create([oldFile], [newFile]);

        Assert.Equal([newFile], plan.ChangedFiles);
        Assert.Equal(0, plan.UnchangedFileCount);
    }

    private static FileManifest File(string path, params byte[] hash) => new()
    {
        Path = ManifestRelativePath.Parse(path),
        Hash = hash
    };
}
