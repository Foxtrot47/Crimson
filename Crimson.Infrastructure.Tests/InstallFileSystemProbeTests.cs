using Crimson.Core;
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
        var result = new InstallFileSystemProbe().Probe(_root);

        Assert.True(result.Success);
        Assert.True(result.AtomicRenameSupported);
        Assert.Equal(InstallFileSystemLocation.Local, result.Location);
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

        var result = new InstallFileSystemProbe().Probe(filePath);

        Assert.False(result.Success);
        Assert.Equal("IOException", result.ErrorType);
        Assert.Equal("keep me", File.ReadAllText(filePath));
        Assert.NotNull(result.CleanupFailures);
        Assert.Empty(result.CleanupFailures);
        Assert.Empty(Directory.EnumerateFiles(_root, ".crimson-write-probe-*"));
    }

    [Fact]
    public void Probe_RejectsLinkedInstallRoot()
    {
        var target = Path.Combine(_root, "target");
        var linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(linkedRoot, target);

        try
        {
            var result = new InstallFileSystemProbe().Probe(linkedRoot);

            Assert.False(result.Success);
            Assert.Equal("InvalidDataException", result.ErrorType);
            Assert.Empty(Directory.EnumerateFiles(target));
        }
        finally
        {
            Directory.Delete(linkedRoot);
        }
    }

    [Fact]
    public void Probe_RejectsReadOnlyRootOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var readOnlyRoot = Path.Combine(_root, "read-only");
        Directory.CreateDirectory(readOnlyRoot);
        File.SetUnixFileMode(
            readOnlyRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = new InstallFileSystemProbe().Probe(readOnlyRoot);

            Assert.False(result.Success);
        }
        finally
        {
            File.SetUnixFileMode(
                readOnlyRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
