namespace Crimson.Infrastructure;

public sealed record InstallFileSystemProbeResult(bool Success, string? ErrorType = null);

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
                stream.WriteByte(0x43);
                stream.Flush(flushToDisk: true);
            }

            File.Move(sourcePath, renamedPath);
            File.Delete(renamedPath);
            return new InstallFileSystemProbeResult(true);
        }
        catch (Exception exception) when (exception is
                   IOException or
                   UnauthorizedAccessException or
                   NotSupportedException)
        {
            TryDelete(sourcePath);
            TryDelete(renamedPath);
            return new InstallFileSystemProbeResult(false, exception.GetType().Name);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
