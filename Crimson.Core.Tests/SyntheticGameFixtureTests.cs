using System.Security.Cryptography;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class SyntheticGameFixtureTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "SyntheticGame");

    [Fact]
    public async Task Fixture_ContainsCompleteOldAndNewLifecycles()
    {
        using var expected = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(FixtureRoot, "expected.json")));
        var root = expected.RootElement;

        var oldManifest = await ReadManifestAsync("old.manifest", root.GetProperty("old"));
        var newManifest = await ReadManifestAsync("new.manifest", root.GetProperty("new"));

        Assert.Equal(root.GetProperty("appName").GetString(), oldManifest.ManifestMeta.AppName);
        Assert.Equal(root.GetProperty("appName").GetString(), newManifest.ManifestMeta.AppName);
        Assert.Equal(root.GetProperty("launchExecutable").GetString(), oldManifest.ManifestMeta.LaunchExe.Value);
        Assert.Equal(root.GetProperty("launchExecutable").GetString(), newManifest.ManifestMeta.LaunchExe.Value);

        Assert.Contains(oldManifest.FileManifestList.Elements, file => file.ChunkParts.Count > 1);
        Assert.Contains(newManifest.FileManifestList.Elements, file => file.ChunkParts.Count > 1);
        Assert.Contains(oldManifest.FileManifestList.Elements, file => file.FileSize == 0);
        Assert.Contains(newManifest.FileManifestList.Elements, file => file.FileSize == 0);
        Assert.Contains(oldManifest.FileManifestList.Elements, file => file.Executable);
        Assert.Contains(newManifest.FileManifestList.Elements, file => file.Executable);
        Assert.True(HasSharedChunk(oldManifest));
        Assert.True(HasSharedChunk(newManifest));

        var plan = ManifestUpdatePlanner.Create(
            oldManifest.FileManifestList.Elements,
            newManifest.FileManifestList.Elements);
        var update = root.GetProperty("update");

        Assert.Equal(update.GetProperty("unchanged").GetArrayLength(), plan.UnchangedFileCount);
        Assert.Equal(
            ReadStringArray(update.GetProperty("changed")),
            plan.ChangedFiles.Select(file => file.Path.Value).Order());
        Assert.Equal(
            ReadStringArray(update.GetProperty("added")),
            plan.AddedFiles.Select(file => file.Path.Value).Order());
        Assert.Equal(
            ReadStringArray(update.GetProperty("removed")),
            plan.RemovedFiles.Select(path => path.Value).Order());
    }

    private static async Task<Manifest> ReadManifestAsync(string name, JsonElement expected)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureRoot, name));
        Assert.Equal(
            expected.GetProperty("manifestSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        var manifest = Manifest.ReadAll(bytes);
        Assert.Equal(expected.GetProperty("buildVersion").GetString(), manifest.ManifestMeta.BuildVersion);
        Assert.Equal(
            expected.GetProperty("downloadSize").GetInt64(),
            manifest.CDL.Elements.Sum(chunk => chunk.FileSize));
        Assert.Equal(
            expected.GetProperty("installSize").GetInt64(),
            manifest.FileManifestList.Elements.Sum(file => file.FileSize));

        var expectedFiles = expected.GetProperty("files");
        Assert.Equal(expectedFiles.EnumerateObject().Count(), manifest.FileManifestList.Elements.Count);
        foreach (var file in manifest.FileManifestList.Elements)
        {
            var expectedFile = expectedFiles.GetProperty(file.Path.Value);
            var contents = await MaterializeAsync(manifest, file);
            Assert.Equal(expectedFile.GetProperty("size").GetInt64(), contents.LongLength);
            Assert.Equal(
                expectedFile.GetProperty("sha1").GetString(),
                Convert.ToHexString(SHA1.HashData(contents)).ToLowerInvariant());
            Assert.Equal(file.ShaHash, SHA1.HashData(contents));
        }

        return manifest;
    }

    private static async Task<byte[]> MaterializeAsync(Manifest manifest, FileManifest file)
    {
        await using var output = new MemoryStream();
        foreach (var part in file.ChunkParts)
        {
            var chunkInfo = manifest.CDL.GetChunkByGuidNum(part.GuidNum);
            var chunkBytes = await File.ReadAllBytesAsync(
                Path.Combine(FixtureRoot, chunkInfo.Path.Replace('/', Path.DirectorySeparatorChar)));
            var chunk = Chunk.ReadBuffer(chunkBytes);
            chunk.ValidateAgainst(chunkInfo);
            var payload = chunk.Data;

            Assert.Equal(chunkInfo.GuidNum, chunk.GuidNum);
            Assert.Equal(chunkInfo.ShaHash, SHA1.HashData(payload));
            await output.WriteAsync(payload.AsMemory(part.Offset, part.Size));
        }

        return output.ToArray();
    }

    private static bool HasSharedChunk(Manifest manifest) => manifest.FileManifestList.Elements
        .SelectMany(file => file.ChunkParts.Select(part => part.GuidNum))
        .GroupBy(guid => guid)
        .Any(group => group.Count() > 1);

    private static IEnumerable<string> ReadStringArray(JsonElement value) => value
        .EnumerateArray()
        .Select(element => element.GetString()!)
        .Order();
}
