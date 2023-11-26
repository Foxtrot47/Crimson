using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Crimson.Models;

public class CustomFields
{
    public int Size { get; set; }
    public byte Version { get; set; }
    public int Count { get; set; }

    private Dictionary<string, string> _dict;

    public CustomFields()
    {
        Size = 0;
        Version = 0;
        Count = 0;
        _dict = new Dictionary<string, string>();
    }

    public string this[string key]
    {
        get => _dict.TryGetValue(key, out var value) ? value : null;
        set => _dict[key] = value;
    }

    public override string ToString()
    {
        return string.Join(", ", _dict);
    }

    public IEnumerable<KeyValuePair<string, string>> Items() => _dict;
    public IEnumerable<string> Keys() => _dict.Keys;
    public IEnumerable<string> Values() => _dict.Values;

    public static CustomFields Read(Stream bio)
    {
        var cf = new CustomFields();
        var reader = new BinaryReader(bio);

        var cfStart = bio.Position;
        cf.Size = reader.ReadInt32();
        cf.Version = reader.ReadByte();
        cf.Count = reader.ReadInt32();

        for (var i = 0; i < cf.Count; i++)
        {
            var key = ReadFString(reader);
            var value = ReadFString(reader);
            cf[key] = value;
        }

        var sizeRead = bio.Position - cfStart;
        if (sizeRead == cf.Size) return cf;
        
        // TODO Log warning here and seek forward
        // downgrade version to prevent issues during serialisation
        cf.Version = 0;
        bio.Seek(cf.Size - sizeRead, SeekOrigin.Current);

        return cf;
    }

    private static string ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
}