using System.Text.Json;
using Crimson.Infrastructure;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class AtomicJsonFileTests : IDisposable
{
    private static readonly JsonStateSchema<TestState> TestSchema =
        new("test", 1, 1024);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-atomic-json-{Guid.NewGuid():N}");

    [Fact]
    public void WriteAndRead_RoundTripsCurrentVersion()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicJsonFile.Write(path, new TestState("current"), TestSchema);
        var result = AtomicJsonFile.Read(path, TestSchema);

        Assert.Equal(JsonStateReadStatus.Success, result.Status);
        Assert.Equal(JsonStateSource.Primary, result.Source);
        Assert.Equal(1, result.Version);
        Assert.Equal("current", result.Value?.Value);
    }

    [Fact]
    public void Read_RecoversLastValidBackup()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicJsonFile.Write(path, new TestState("first"), TestSchema);
        AtomicJsonFile.Write(path, new TestState("second"), TestSchema);
        File.WriteAllText(path, "{broken");

        var result = AtomicJsonFile.Read(path, TestSchema);

        Assert.Equal(JsonStateReadStatus.Success, result.Status);
        Assert.Equal(JsonStateSource.Backup, result.Source);
        Assert.Equal("first", result.Value?.Value);
        var repaired = AtomicJsonFile.ReadAndMigrate(path, TestSchema);
        Assert.Equal(JsonStateSource.Primary, repaired.Source);
        File.Delete(path + ".bak");
        Assert.Equal("first", AtomicJsonFile.Read(path, TestSchema).Value?.Value);
    }

    [Fact]
    public void Read_MigratesRawLegacyStateAndRetainsItAsBackup()
    {
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, JsonSerializer.Serialize(new TestState("legacy")));

        var legacy = AtomicJsonFile.Read(path, TestSchema);
        Assert.Equal(JsonStateReadStatus.Success, legacy.Status);
        Assert.Equal(0, legacy.Version);

        var migrated = AtomicJsonFile.ReadAndMigrate(path, TestSchema);

        Assert.Equal(1, migrated.Version);
        Assert.Equal("legacy", migrated.Value?.Value);
        Assert.Equal(
            "legacy",
            JsonSerializer.Deserialize<TestState>(File.ReadAllText(path + ".bak"))?.Value);
        var migratedJson = File.ReadAllText(path);
        AtomicJsonFile.ReadAndMigrate(path, TestSchema);
        Assert.Equal(migratedJson, File.ReadAllText(path));
    }

    [Fact]
    public void ReadAndWrite_RejectUnknownFutureVersionWithoutModification()
    {
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(_root);
        const string future = "{\"Version\":2,\"Data\":{\"Value\":\"future\"}}";
        File.WriteAllText(path, future);

        var result = AtomicJsonFile.Read(path, TestSchema);

        Assert.Equal(JsonStateReadStatus.UnsupportedVersion, result.Status);
        Assert.Equal(2, result.Version);
        Assert.Throws<NotSupportedException>(() =>
            AtomicJsonFile.Write(path, new TestState("replacement"), TestSchema));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void ReadAndWrite_EnforceCategorySizeLimit()
    {
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(_root);
        var smallSchema = new JsonStateSchema<TestState>("small", 1, 32);
        File.WriteAllText(path, new string('x', 33));

        Assert.Equal(JsonStateReadStatus.Corrupt, AtomicJsonFile.Read(path, smallSchema).Status);
        Assert.Throws<InvalidDataException>(() =>
            AtomicJsonFile.Write(path, new TestState(new string('x', 64)), smallSchema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsSchema_MigratesHistoricalFormats(bool nestedVersionOne)
    {
        var path = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
        var settings = new Settings
        {
            MicaEnabled = true,
            DefaultInstallLocation = @"E:\Games"
        };
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(
            path,
            nestedVersionOne
                ? JsonSerializer.Serialize(new { Version = 1, Data = json })
                : json);

        var result = AtomicJsonFile.Read(path, JsonStateSchemas.Settings);

        Assert.Equal(JsonStateReadStatus.Success, result.Status);
        Assert.Equal(nestedVersionOne ? 1 : 0, result.Version);
        Assert.True(result.Value?.MicaEnabled);
        Assert.Equal(@"E:\Games", result.Value?.DefaultInstallLocation);
    }

    [Fact]
    public void StateCatalog_HasUniqueBoundedCategories()
    {
        Assert.Equal(
            JsonStateSchemas.Catalog.Count,
            JsonStateSchemas.Catalog.Select(category => category.Category).Distinct().Count());
        Assert.All(JsonStateSchemas.Catalog, category =>
        {
            Assert.True(category.CurrentVersion > 0);
            Assert.True(category.MaximumBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(category.Owner));
            Assert.False(string.IsNullOrWhiteSpace(category.RelativePath));
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record TestState(string Value);
}
