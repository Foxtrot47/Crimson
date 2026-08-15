namespace Crimson.Models;

public sealed class CustomFields
{
    public int Size { get; internal set; }
    public byte Version { get; internal set; }
    public int Count { get; internal set; }

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;
        }
    }

    public override string ToString() => string.Join(", ", _values);

    public IEnumerable<KeyValuePair<string, string>> Items() => _values;
    public IEnumerable<string> Keys() => _values.Keys;
    public IEnumerable<string> Values() => _values.Values;

    public static CustomFields Read(Stream stream) => Read(new EpicBinaryReader(stream));

    internal static CustomFields Read(EpicBinaryReader reader)
    {
        var start = reader.Position;
        var end = reader.BeginSection("Custom fields");
        var fields = new CustomFields
        {
            Size = checked((int)(end - start)),
            Version = reader.ReadByte(),
            Count = reader.ReadCount(EpicProtocolLimits.MaximumCustomFields, "Custom field")
        };
        if (fields.Version > 3)
            throw new InvalidDataException($"Custom field version {fields.Version} is unsupported.");

        var keys = new string[fields.Count];
        for (var index = 0; index < fields.Count; index++)
            keys[index] = reader.ReadUnrealString();
        for (var index = 0; index < fields.Count; index++)
        {
            var key = keys[index];
            var value = reader.ReadUnrealString();
            if (string.IsNullOrEmpty(key) || !fields._values.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate or empty custom field key: {key}.");
        }

        reader.EndSection(end, "Custom fields");
        return fields;
    }
}
