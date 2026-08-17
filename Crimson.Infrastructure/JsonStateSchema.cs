using System.Text.Json;

namespace Crimson.Infrastructure;

public enum JsonStateReadStatus
{
    Success,
    Missing,
    Corrupt,
    UnsupportedVersion
}

public enum JsonStateSource
{
    None,
    Primary,
    Backup
}

public sealed record JsonStateReadResult<T>(
    JsonStateReadStatus Status,
    T? Value = default,
    int? Version = null,
    JsonStateSource Source = JsonStateSource.None,
    string? Error = null)
{
    public bool IsSuccess => Status == JsonStateReadStatus.Success;
}

public sealed class JsonStateSchema<T>
{
    private readonly Func<int, JsonElement, JsonSerializerOptions?, T?> _deserialize;

    public JsonStateSchema(
        string category,
        int currentVersion,
        long maximumBytes,
        Func<int, JsonElement, JsonSerializerOptions?, T?>? deserialize = null)
    {
        Category = string.IsNullOrWhiteSpace(category)
            ? throw new ArgumentException("State category is required.", nameof(category))
            : category;
        CurrentVersion = currentVersion > 0
            ? currentVersion
            : throw new ArgumentOutOfRangeException(nameof(currentVersion));
        MaximumBytes = maximumBytes > 0
            ? maximumBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _deserialize = deserialize ?? DeserializeDefault;
    }

    public string Category { get; }

    public int CurrentVersion { get; }

    public long MaximumBytes { get; }

    internal T? Deserialize(
        int version,
        JsonElement data,
        JsonSerializerOptions? options) => _deserialize(version, data, options);

    private static T? DeserializeDefault(
        int version,
        JsonElement data,
        JsonSerializerOptions? options) => data.Deserialize<T>(options);
}

public sealed record JsonStateCategoryDescriptor(
    string Category,
    string RelativePath,
    string Owner,
    int CurrentVersion,
    long MaximumBytes,
    bool Authoritative,
    string Notes);
