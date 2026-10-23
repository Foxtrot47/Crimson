using System.Numerics;
using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallerUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-update-{Guid.NewGuid():N}");
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly InstallManager _installer;

    public InstallerUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _installer = new InstallManager(_logger, null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public async Task CopyTaskTruncatesAChangedFileToManifestLength()
    {
        var destination = Path.Combine(_root, "changed.bin");
        await File.WriteAllBytesAsync(destination, [1, 2, 3, 4, 5]);
        var chunkPath = Path.Combine(_root, "chunk.bin");
        await File.WriteAllBytesAsync(chunkPath, CreateUncompressedChunk([9, 8, 7]));
        var task = new IoTask {
            SourceFilePath = chunkPath,
            DestinationFilePath = destination,
            TaskType = IoTaskType.Copy,
            Size = 3,
            DestinationFileSize = 3,
            GuidNum = BigInteger.Zero,
            SourceChunkGuidNum = BigInteger.Zero
        };
        var method = typeof(InstallManager).GetMethod("ProcessCopyTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var install = new InstallItem("synthetic", ActionType.Update, _root);
        typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(_installer, install);

        await ((Task)method.Invoke(_installer, [task, _root])!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(3L, typeof(TransferRateHistory).GetField("_writtenBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(install.TransferRates));
        Assert.Equal(0L, typeof(TransferRateHistory).GetField("_downloadedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(install.TransferRates));
    }

    [Fact]
    public async Task DiskWriteTelemetryExcludesPreallocation()
    {
        var destination = Path.Combine(_root, "preallocated.bin");
        var chunkPath = Path.Combine(_root, "chunk.bin");
        await File.WriteAllBytesAsync(chunkPath, CreateUncompressedChunk([1, 2, 3]));
        var task = new IoTask
        {
            SourceFilePath = chunkPath, DestinationFilePath = destination, TaskType = IoTaskType.Copy,
            Size = 3, DestinationFileSize = 100, GuidNum = BigInteger.Zero, SourceChunkGuidNum = BigInteger.Zero
        };
        var install = new InstallItem("synthetic", ActionType.Install, _root);
        typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(_installer, install);
        var method = typeof(InstallManager).GetMethod("ProcessCopyTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await ((Task)method.Invoke(_installer, [task, _root])!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, new FileInfo(destination).Length);
        Assert.Equal(3L, typeof(TransferRateHistory).GetField("_writtenBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(install.TransferRates));
    }

    [Fact]
    public void VerificationFailureCannotReportSuccess()
    {
        var install = new InstallItem("test", ActionType.Update, _root);
        var method = typeof(InstallManager).GetMethod("EnsureVerificationSucceeded", BindingFlags.Static | BindingFlags.NonPublic)!;

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [install, 2]));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal("2 files failed verification", install.StatusMessage);
    }

    [Theory]
    [InlineData(0L, 4L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 1L)]
    [InlineData(-1L, 1L)]
    [InlineData(0L, -1L)]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(1L, long.MaxValue)]
    public async Task CopyTaskRejectsInvalidPartRangesWithoutChangingTheDestination(long offset, long size)
    {
        var destination = Path.Combine(_root, "unchanged.bin");
        byte[] original = [4, 5, 6, 7, 8];
        await File.WriteAllBytesAsync(destination, original);
        var chunkPath = Path.Combine(_root, "invalid-part.chunk");
        await File.WriteAllBytesAsync(chunkPath, CreateUncompressedChunk([1, 2, 3]));
        var task = new IoTask
        {
            SourceFilePath = chunkPath, DestinationFilePath = destination,
            TaskType = IoTaskType.Copy, Offset = offset, Size = size, DestinationFileSize = 3
        };
        var method = typeof(InstallManager).GetMethod("ProcessCopyTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ((Task)method.Invoke(_installer, [task, _root])!).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.True(File.Exists(chunkPath));
    }

    [Fact]
    public async Task CancelledCopyDoesNotChangeTheDestination()
    {
        var destination = Path.Combine(_root, "cancelled.bin");
        byte[] original = [4, 5, 6, 7];
        await File.WriteAllBytesAsync(destination, original);
        var chunkPath = Path.Combine(_root, "cancelled.chunk");
        await File.WriteAllBytesAsync(chunkPath, CreateUncompressedChunk([1, 2, 3]));
        var cancellation = (CancellationTokenSource)typeof(InstallManager)
            .GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_installer)!;
        cancellation.Cancel();
        var task = new IoTask
        {
            SourceFilePath = chunkPath, DestinationFilePath = destination,
            TaskType = IoTaskType.Copy, Size = 3, DestinationFileSize = 3
        };
        var method = typeof(InstallManager).GetMethod("ProcessCopyTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ((Task)method.Invoke(_installer, [task, _root])!).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    private static byte[] CreateUncompressedChunk(byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xB1FE3AA2u);
        writer.Write(3u);
        writer.Write(66u);
        writer.Write((uint)payload.Length);
        for (var index = 0; index < 4; index++) writer.Write(0u);
        writer.Write(0UL);
        writer.Write((byte)0);
        writer.Write(new byte[20]);
        writer.Write((byte)0);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        return stream.ToArray();
    }

    public void Dispose()
    {
        _installer.Dispose();
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
