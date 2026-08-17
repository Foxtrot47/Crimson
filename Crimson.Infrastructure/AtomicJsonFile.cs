using System.Text.Json;

namespace Crimson.Infrastructure;

public static class AtomicJsonFile
{
    public const int CurrentVersion = 1;

    public static bool TryRead<T>(
        string path,
        out T? value,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (TryReadPath(path, out value, options))
            return true;

        return TryReadPath(GetBackupPath(path), out value, options);
    }

    public static void Write<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("JSON state path has no parent directory.");
        Directory.CreateDirectory(directory);

        var envelope = new JsonStateEnvelope<T>(CurrentVersion, value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, options);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81_920,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (TryReadPath<T>(fullPath, out _, options))
                File.Copy(fullPath, GetBackupPath(fullPath), overwrite: true);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool TryReadPath<T>(
        string path,
        out T? value,
        JsonSerializerOptions? options)
    {
        value = default;
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = JsonSerializer.Deserialize<JsonStateEnvelope<T>>(stream, options);
            if (envelope is null || envelope.Version != CurrentVersion)
                return false;

            value = envelope.Data;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string GetBackupPath(string path) => path + ".bak";

    private sealed record JsonStateEnvelope<T>(int Version, T Data);
}
