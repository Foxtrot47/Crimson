using Crimson.Utils;

namespace Crimson.Tests;

public sealed class ManifestPathTests
{
    [Fact]
    public void ResolveUnderRoot_ResolvesNestedRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        var resolved = ManifestPath.ResolveUnderRoot(root, "folder/file.bin");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "folder", "file.bin")), resolved);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData(@"..\outside.bin")]
    [InlineData("folder/../../outside.bin")]
    [InlineData(@"folder\..\outside.bin")]
    [InlineData(@"folder/..\outside.bin")]
    [InlineData(@"folder\../outside.bin")]
    [InlineData("folder/../file.bin")]
    [InlineData("folder/.")]
    [InlineData("folder/")]
    [InlineData(".")]
    [InlineData("")]
    public void ResolveUnderRoot_RejectsInvalidSegments(string manifestPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, manifestPath));
    }

    [Theory]
    [InlineData(@"\outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData(@"C:\outside.bin")]
    [InlineData(@"C:outside.bin")]
    [InlineData(@"\\server\share\outside.bin")]
    [InlineData(@"\\?\C:\outside.bin")]
    [InlineData(@"\\.\C:\outside.bin")]
    public void ResolveUnderRoot_RejectsWindowsRootedForms(string manifestPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, manifestPath));
    }

    [Theory]
    [InlineData("file.bin:payload")]
    [InlineData("NUL")]
    [InlineData("CON.txt")]
    [InlineData(@"folder\COM1.bin")]
    [InlineData("file.bin.")]
    [InlineData("file.bin ")]
    public void ResolveUnderRoot_RejectsWindowsSpecialFileNames(string manifestPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, manifestPath));
    }

    [Fact]
    public void ManifestRelativePath_NormalizesLogicalSegments()
    {
        var path = ManifestRelativePath.Parse("Folder\\Cafe\u0301/file.bin");

        Assert.Equal("Folder/Caf\u00E9/file.bin", path.Value);
        Assert.Equal(["Folder", "Caf\u00E9", "file.bin"], path.Segments);
    }

    [Theory]
    [MemberData(nameof(CollidingManifests))]
    public void ValidateManifest_RejectsCrossPlatformCollisions(string[] paths)
    {
        Assert.Throws<InvalidDataException>(() => ManifestPath.ValidateManifest(paths));
    }

    public static TheoryData<string[]> CollidingManifests => new()
    {
        new string[] { "Data/File.bin", "data/file.bin" },
        new string[] { "Caf\u00E9.bin", "Cafe\u0301.bin" },
        new string[] { "Data", "data/file.bin" }
    };

    [Fact]
    public void ResolveUnderRoot_RejectsExistingReparsePoint()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-path-test-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "root");
        var outside = Path.Combine(sandbox, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        try
        {
            Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, "link/victim.bin"));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void RevalidateUnderRoot_RejectsNewReparsePoint()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-path-test-{Guid.NewGuid():N}");
        var root = Path.Combine(sandbox, "root");
        var outside = Path.Combine(sandbox, "outside");
        var linkedDirectory = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var candidate = ManifestPath.ResolveUnderRoot(root, "linked/victim.bin");
        Directory.CreateSymbolicLink(linkedDirectory, outside);

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                ManifestPath.RevalidateUnderRoot(root, candidate));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void RevalidateUnderRoot_RejectsLinkedInstallRoot()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"crimson-path-test-{Guid.NewGuid():N}");
        var target = Path.Combine(sandbox, "target");
        var linkedRoot = Path.Combine(sandbox, "linked-root");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(linkedRoot, target);

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                ManifestPath.RevalidateUnderRoot(linkedRoot, linkedRoot));
        }
        finally
        {
            Directory.Delete(linkedRoot);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void RevalidateUnderRoot_RejectsPathOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-path-test-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"crimson-outside-{Guid.NewGuid():N}");

        Assert.Throws<InvalidDataException>(() =>
            ManifestPath.RevalidateUnderRoot(root, outside));
    }

    [Fact]
    public void ResolveExistingImportFile_UsesSingleCaseInsensitiveMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-import-test-{Guid.NewGuid():N}");
        var actualPath = Path.Combine(root, "Data", "CONFIG.json");
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, "config");

        try
        {
            var resolved = ManifestPath.ResolveExistingImportFile(
                root,
                ManifestRelativePath.Parse("data/config.JSON"));

            Assert.NotNull(resolved);
            Assert.Equal("config", File.ReadAllText(resolved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExistingImportFile_ReturnsNullForMissingSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            Assert.Null(ManifestPath.ResolveExistingImportFile(
                root,
                ManifestRelativePath.Parse("missing/file.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExistingImportFile_RejectsAmbiguousCaseInsensitiveMatch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"crimson-import-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        Directory.CreateDirectory(Path.Combine(root, "data"));

        try
        {
            Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveExistingImportFile(
                root,
                ManifestRelativePath.Parse("DATA/file.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveUnderRoot_SupportsLongNestedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimson-long-path-{Guid.NewGuid():N}");
        var logicalPath = string.Join('/', Enumerable.Repeat("segment0123456789", 16)) + "/file.bin";
        var resolved = ManifestPath.ResolveUnderRoot(root, logicalPath);
        Assert.True(resolved.Length > 260);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            File.WriteAllText(resolved, "long-path");
            Assert.Equal("long-path", File.ReadAllText(resolved));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveUnderRoot_RejectsMalformedPathAsInvalidManifestData()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, "file.bin\0suffix"));
    }
}
