using System.Text.Json;

namespace Crimson.Infrastructure;

public static class AtomicJsonFile
{
    public static JsonStateReadResult<T> Read<T>(
        string path,
        JsonStateSchema<T> schema,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(schema);
        var primary = ReadPath(path, schema, JsonStateSource.Primary, options);
        if (primary.Status is JsonStateReadStatus.Success or JsonStateReadStatus.UnsupportedVersion)
            return primary;

        var backup = ReadPath(GetBackupPath(path), schema, JsonStateSource.Backup, options);
        if (backup.Status is JsonStateReadStatus.Success or JsonStateReadStatus.UnsupportedVersion)
            return backup;
        if (primary.Status == JsonStateReadStatus.Corrupt)
            return primary;
        return backup.Status == JsonStateReadStatus.Corrupt
            ? backup
            : primary;
    }

    public static JsonStateReadResult<T> ReadAndMigrate<T>(
        string path,
        JsonStateSchema<T> schema,
        JsonSerializerOptions? options = null)
    {
        var result = Read(path, schema, options);
        if (!result.IsSuccess || result.Value is null ||
            (result.Version == schema.CurrentVersion && result.Source == JsonStateSource.Primary))
            return result;

        Write(path, result.Value, schema, options);
        return Read(path, schema, options);
    }

    public static void Write<T>(
        string path,
        T value,
        JsonStateSchema<T> schema,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(schema);
        var current = Read(path, schema, options);
        if (current.Status == JsonStateReadStatus.UnsupportedVersion)
            throw new NotSupportedException(
                $"State category '{schema.Category}' uses unsupported schema version {current.Version}.");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("JSON state path has no parent directory.");
        Directory.CreateDirectory(directory);

        var envelope = new JsonStateEnvelope<T>(schema.CurrentVersion, value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, options);
        if (bytes.LongLength > schema.MaximumBytes)
            throw new InvalidDataException(
                $"State category '{schema.Category}' exceeds its {schema.MaximumBytes}-byte limit.");

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

            var primary = ReadPath(fullPath, schema, JsonStateSource.Primary, options);
            if (primary.IsSuccess)
                File.Copy(fullPath, GetBackupPath(fullPath), overwrite: true);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static JsonStateReadResult<T> ReadPath<T>(
        string path,
        JsonStateSchema<T> schema,
        JsonStateSource source,
        JsonSerializerOptions? options)
    {
        if (!File.Exists(path))
            return new JsonStateReadResult<T>(JsonStateReadStatus.Missing, Source: source);

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > schema.MaximumBytes)
            {
                return new JsonStateReadResult<T>(
                    JsonStateReadStatus.Corrupt,
                    Source: source,
                    Error: $"State exceeds the {schema.MaximumBytes}-byte category limit.");
            }

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var version = 0;
            var data = root;
            if (HasEnvelopeShape(root))
            {
                if (!TryGetEnvelope(root, out var envelopeVersion, out var envelopeData))
                    throw new InvalidDataException("State envelope is malformed.");
                version = envelopeVersion;
                data = envelopeData;
                if (version > schema.CurrentVersion)
                {
                    return new JsonStateReadResult<T>(
                        JsonStateReadStatus.UnsupportedVersion,
                        Version: version,
                        Source: source,
                        Error: $"Schema version {version} is newer than supported version {schema.CurrentVersion}.");
                }
            }

            var value = schema.Deserialize(version, data, options);
            return value is null
                ? new JsonStateReadResult<T>(
                    JsonStateReadStatus.Corrupt,
                    Version: version,
                    Source: source,
                    Error: "State data was null.")
                : new JsonStateReadResult<T>(
                    JsonStateReadStatus.Success,
                    value,
                    version,
                    source);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return new JsonStateReadResult<T>(
                JsonStateReadStatus.Corrupt,
                Source: source,
                Error: exception.GetType().Name);
        }
    }

    private static bool HasEnvelopeShape(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        TryGetProperty(root, "Version", "version", out _) &&
        TryGetProperty(root, "Data", "data", out _);

    private static bool TryGetEnvelope(
        JsonElement root,
        out int version,
        out JsonElement data)
    {
        version = 0;
        data = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, "Version", "version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out version) ||
            version <= 0 ||
            !TryGetProperty(root, "Data", "data", out data))
            return false;
        return true;
    }

    private static bool TryGetProperty(
        JsonElement root,
        string primaryName,
        string alternateName,
        out JsonElement value) =>
        root.TryGetProperty(primaryName, out value) || root.TryGetProperty(alternateName, out value);

    private static string GetBackupPath(string path) => path + ".bak";

    private sealed record JsonStateEnvelope<T>(int Version, T Data);
}
