namespace Crimson.Infrastructure;

public sealed record InstallFileSystemCleanupFailure(string FileName, string ErrorType);

public sealed record InstallFileSystemProbeResult(
    bool Success,
    string? ErrorType = null,
    string? VolumeIdentity = null,
    long? AvailableBytes = null,
    long? TotalBytes = null,
    bool AtomicRenameSupported = false,
    IReadOnlyList<InstallFileSystemCleanupFailure>? CleanupFailures = null);

public static class InstallFileSystemProbe
{
    private const string ProbePrefix = ".crimson-write-probe-";

    public static InstallFileSystemProbeResult Probe(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var root = Path.GetFullPath(directoryPath);
        var probeId = Guid.NewGuid().ToString("N");
        var sourcePath = Path.Combine(root, $"{ProbePrefix}{probeId}.tmp");
        var renamedPath = Path.Combine(root, $"{ProbePrefix}{probeId}.renamed");
        var sourceCreated = false;
        var renamedCreated = false;
        try
        {
            Directory.CreateDirectory(root);
            using (var stream = new FileStream(
                       sourcePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.WriteThrough))
            {
                sourceCreated = true;
                stream.WriteByte(0x43);
                stream.Flush(flushToDisk: true);
            }

            File.Move(sourcePath, renamedPath);
            sourceCreated = false;
            renamedCreated = true;
            File.Delete(renamedPath);
            renamedCreated = false;
            var (volumeIdentity, availableBytes, totalBytes) = GetDriveCapacity(root);
            return new InstallFileSystemProbeResult(
                true,
                VolumeIdentity: volumeIdentity,
                AvailableBytes: availableBytes,
                TotalBytes: totalBytes,
                AtomicRenameSupported: true);
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException)
        {
            var cleanupFailures = new List<InstallFileSystemCleanupFailure>(2);
            AddCleanupFailure(sourceCreated, sourcePath, cleanupFailures);
            AddCleanupFailure(renamedCreated, renamedPath, cleanupFailures);
            return new InstallFileSystemProbeResult(
                false,
                exception.GetType().Name,
                CleanupFailures: cleanupFailures);
        }
    }

    private static (string? Identity, long? AvailableBytes, long? TotalBytes) GetDriveCapacity(
        string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return default;
            var drive = new DriveInfo(root);
            return drive.IsReady
                ? (drive.Name, drive.AvailableFreeSpace, drive.TotalSize)
                : default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static void AddCleanupFailure(
        bool artifactCreated,
        string path,
        List<InstallFileSystemCleanupFailure> failures)
    {
        if (!artifactCreated)
            return;
        var failure = TryDelete(path);
        if (failure is not null)
            failures.Add(failure);
    }

    private static InstallFileSystemCleanupFailure? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return File.Exists(path)
                ? new InstallFileSystemCleanupFailure(Path.GetFileName(path), "FileStillExists")
                : null;
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException)
        {
            return new InstallFileSystemCleanupFailure(
                Path.GetFileName(path),
                exception.GetType().Name);
        }
    }
}
