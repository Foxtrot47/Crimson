using Crimson.Utils;

namespace Crimson.Tests;

public sealed class InstallDirectoryCleanupTests
{
    [Fact]
    public void RemoveEmptyOwnedDirectories_RemovesTempDirectoriesAndEmptyRoot()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "Data", "Nested"));
        var tempDirectory = Path.Combine(root, ".Crimson");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllBytes(Path.Combine(tempDirectory, "stale.chunk"), [1, 2, 3]);

        InstallDirectoryCleanup.RemoveEmptyOwnedDirectories(
            root,
            ["Game.exe", "Data/Nested/config.json"]);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RemoveEmptyOwnedDirectories_PreservesUntrackedFiles()
    {
        var root = CreateRoot();
        var keptDirectory = Path.Combine(root, "Data", "Nested");
        var removedDirectory = Path.Combine(root, "Empty", "Owned");
        Directory.CreateDirectory(keptDirectory);
        Directory.CreateDirectory(removedDirectory);
        var untrackedFile = Path.Combine(keptDirectory, "user-file.txt");
        File.WriteAllText(untrackedFile, "keep");
        Directory.CreateDirectory(Path.Combine(root, ".Crimson"));

        try
        {
            InstallDirectoryCleanup.RemoveEmptyOwnedDirectories(
                root,
                ["Data/Nested/owned.bin", "Empty/Owned/file.bin"]);

            Assert.True(File.Exists(untrackedFile));
            Assert.False(Directory.Exists(removedDirectory));
            Assert.False(Directory.Exists(Path.Combine(root, ".Crimson")));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveEmptyOwnedDirectories_RejectsEscapingManifestPath()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-cleanup-test-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "game");
        var outside = Path.Combine(sandbox, "outside.txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(outside, "keep");

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                InstallDirectoryCleanup.RemoveEmptyOwnedDirectories(root, ["../outside.txt"]));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-cleanup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
