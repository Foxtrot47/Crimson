namespace Crimson.Core;

public enum InstallFileSystemLocation
{
    Unknown,
    Local,
    Network
}

public sealed record InstallFileSystemCleanupFailure(string FileName, string ErrorType);

public sealed record InstallFileSystemProbeResult(
    bool Success,
    string? ErrorType = null,
    string? VolumeIdentity = null,
    long? AvailableBytes = null,
    long? TotalBytes = null,
    bool AtomicRenameSupported = false,
    InstallFileSystemLocation Location = InstallFileSystemLocation.Unknown,
    IReadOnlyList<InstallFileSystemCleanupFailure>? CleanupFailures = null);

public interface IInstallFileSystemProbe
{
    InstallFileSystemProbeResult Probe(string directoryPath);
}
