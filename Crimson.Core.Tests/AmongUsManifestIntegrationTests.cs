using System.Security.Cryptography;
using Crimson.Models;
using Crimson.Utils;
using Xunit;

namespace Crimson.Tests;

public sealed class AmongUsManifestIntegrationTests
{
    private const string AppName = "963137e4c29d4c79a81323b8fab03a40";
    private const string BuildVersion = "6803";
    private const long DownloadSize = 640_877_835;
    private const long InstallSize = 1_033_953_085;
    private const string ManifestSha256 =
        "d46d0e8ac0f0140ba2017374ff3c34d333200ec4b7b69ce20f09444d52f632da";

    [LocalAmongUsManifestFact]
    public async Task CachedManifest_MatchesApprovedCandidateAndHasValidReferences()
    {
        var path = LocalAmongUsManifestFactAttribute.ManifestPath!;
        var manifestBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(ManifestSha256, Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
        var manifest = Manifest.ReadAll(manifestBytes);

        Assert.Equal(AppName, manifest.ManifestMeta.AppName);
        Assert.Equal(BuildVersion, manifest.ManifestMeta.BuildVersion);
        Assert.Equal("Among Us.exe", manifest.ManifestMeta.LaunchExe);
        Assert.Equal(101, manifest.FileManifestList.Elements.Count);
        Assert.Equal(977, manifest.CDL.Elements.Count);
        Assert.Equal(DownloadSize, manifest.CDL.Elements.Sum(chunk => chunk.FileSize));
        Assert.Equal(InstallSize, manifest.FileManifestList.Elements.Sum(file => file.FileSize));

        var root = Path.Combine(Path.GetTempPath(), $"crimson-among-us-{Guid.NewGuid():N}");
        var launchPath = ManifestPath.ResolveUnderRoot(root, manifest.ManifestMeta.LaunchExe);
        var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.FileManifestList.Elements)
        {
            var resolvedPath = ManifestPath.ResolveUnderRoot(root, file.Filename);
            Assert.True(resolvedPaths.Add(resolvedPath), $"Duplicate destination path: {file.Filename}");

            foreach (var part in file.ChunkParts)
            {
                var chunk = manifest.CDL.GetChunkByGuidNum(part.GuidNum);
                Assert.InRange(part.Offset, 0, chunk.WindowSize);
                Assert.InRange(part.Size, 0, chunk.WindowSize - part.Offset);
                Assert.InRange(part.FileOffset, 0, file.FileSize);
                Assert.InRange(part.Size, 0, file.FileSize - part.FileOffset);
            }
        }

        Assert.Contains(launchPath, resolvedPaths);
    }
}

public sealed class LocalAmongUsManifestFactAttribute : FactAttribute
{
    public static string? ManifestPath { get; } = FindManifest();

    public LocalAmongUsManifestFactAttribute()
    {
        if (ManifestPath == null)
            Skip = "Set CRIMSON_AMONG_US_MANIFEST to the cached Among Us build 6803 manifest.";
    }

    private static string? FindManifest()
    {
        var configured = Environment.GetEnvironmentVariable("CRIMSON_AMONG_US_MANIFEST");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cached = Path.Combine(
            localAppData,
            "Crimson",
            "manifests",
            "963137e4c29d4c79a81323b8fab03a40_6803.manifest");
        return File.Exists(cached) ? cached : null;
    }
}
