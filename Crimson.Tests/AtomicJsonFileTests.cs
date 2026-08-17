using System.Text.Json;
using Crimson.Infrastructure;

namespace Crimson.Tests;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-atomic-json-{Guid.NewGuid():N}");

    [Fact]
    public void WriteAndRead_RoundTripsCurrentVersion()
    {
        var path = Path.Combine(_root, "state.json");

        AtomicJsonFile.Write(path, new TestState("current"));

        Assert.True(AtomicJsonFile.TryRead<TestState>(path, out var state));
        Assert.Equal("current", state?.Value);
    }

    [Fact]
    public void Read_RecoversLastValidBackup()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicJsonFile.Write(path, new TestState("first"));
        AtomicJsonFile.Write(path, new TestState("second"));
        File.WriteAllText(path, "{broken");

        Assert.True(AtomicJsonFile.TryRead<TestState>(path, out var state));
        Assert.Equal("first", state?.Value);
    }

    [Fact]
    public void Read_RejectsUnversionedAndUnknownState()
    {
        var path = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, JsonSerializer.Serialize(new TestState("legacy")));
        Assert.False(AtomicJsonFile.TryRead<TestState>(path, out _));

        File.WriteAllText(path, "{\"Version\":2,\"Data\":{\"Value\":\"future\"}}");
        Assert.False(AtomicJsonFile.TryRead<TestState>(path, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record TestState(string Value);
}
