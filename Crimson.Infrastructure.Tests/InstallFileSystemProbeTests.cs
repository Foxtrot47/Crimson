using Crimson.Infrastructure;
using Xunit;

namespace Crimson.Infrastructure.Tests;

public sealed class InstallFileSystemProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"crimson-fs-probe-{Guid.NewGuid():N}");

    [Fact]
    public void Probe_VerifiesWriteFlushRenameAndDelete()
    {
        var result = InstallFileSystemProbe.Probe(_root);

        Assert.True(result.Success);
        Assert.True(result.AtomicRenameSupported);
        Assert.False(string.IsNullOrWhiteSpace(result.VolumeIdentity));
        Assert.True(result.AvailableBytes > 0);
        Assert.True(result.TotalBytes >= result.AvailableBytes);
        Assert.Null(result.ErrorType);
        Assert.Null(result.CleanupFailures);
        Assert.Empty(Directory.EnumerateFiles(_root, ".crimson-write-probe-*"));
    }

    [Fact]
    public void Probe_FailsWithoutChangingAnExistingFile()
    {
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "occupied");
        File.WriteAllText(filePath, "keep me");

        var result = InstallFileSystemProbe.Probe(filePath);

        Assert.False(result.Success);
        Assert.Equal("IOException", result.ErrorType);
        Assert.Equal("keep me", File.ReadAllText(filePath));
        Assert.NotNull(result.CleanupFailures);
        Assert.Empty(result.CleanupFailures);
        Assert.Empty(Directory.EnumerateFiles(_root, ".crimson-write-probe-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
