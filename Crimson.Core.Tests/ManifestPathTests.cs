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
    public void ResolveUnderRoot_RejectsMalformedPathAsInvalidManifestData()
    {
        var root = Path.Combine(Path.GetTempPath(), "crimson-path-test", "game");

        Assert.Throws<InvalidDataException>(() => ManifestPath.ResolveUnderRoot(root, "file.bin\0suffix"));
    }
}
