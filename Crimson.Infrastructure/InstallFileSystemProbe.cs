using Crimson.Core;
using Crimson.Utils;

namespace Crimson.Infrastructure;

public sealed class InstallFileSystemProbe : IInstallFileSystemProbe
{
    private const string ProbePrefix = ".crimson-write-probe-";

    public InstallFileSystemProbeResult Probe(string directoryPath)
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
            root = ManifestPath.RevalidateUnderRoot(root, root);
            Directory.CreateDirectory(root);
            root = ManifestPath.RevalidateUnderRoot(root, root);
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
            var (volumeIdentity, availableBytes, totalBytes, location) = GetDriveCapacity(root);
            return new InstallFileSystemProbeResult(
                true,
                VolumeIdentity: volumeIdentity,
                AvailableBytes: availableBytes,
                TotalBytes: totalBytes,
                AtomicRenameSupported: true,
                Location: location);
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException or
                   InvalidDataException)
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

    private static (
        string? Identity,
        long? AvailableBytes,
        long? TotalBytes,
        InstallFileSystemLocation Location) GetDriveCapacity(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives()
                .Where(candidate => IsUnderRoot(fullPath, candidate.RootDirectory.FullName, comparison))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null || !drive.IsReady)
                return default;
            return (
                drive.Name,
                drive.AvailableFreeSpace,
                drive.TotalSize,
                drive.DriveType switch
                {
                    DriveType.Fixed or DriveType.Removable or DriveType.Ram =>
                        InstallFileSystemLocation.Local,
                    DriveType.Network => InstallFileSystemLocation.Network,
                    _ => InstallFileSystemLocation.Unknown
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static bool IsUnderRoot(
        string path,
        string root,
        StringComparison comparison)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison));
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
