using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft.Protocol;

// Small bounded wire codec for the reverse-engineered messages. Unknown fields remain skippable.
internal static class UbisoftWire
{
    internal const int MaximumFrameSize = 16 * 1024 * 1024;
    internal sealed record Field(int Number, int WireType, ulong Integer, byte[] Data);

    internal static byte[] Message(params byte[][] fields) => fields.SelectMany(f => f).ToArray();
    internal static byte[] Integer(int field, ulong value) => Message(Varint((ulong)field << 3), Varint(value));
    internal static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    internal static byte[] Bytes(int field, byte[] value) => Message(Varint(((ulong)field << 3) | 2), Varint((ulong)value.Length), value);
    internal static byte[] Float(int field, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return Message(Varint(((ulong)field << 3) | 5), bytes);
    }
    private static byte[] Varint(ulong value)
    {
        var result = new List<byte>();
        do { result.Add((byte)((value & 127) | (value > 127 ? 128UL : 0))); value >>= 7; } while (value != 0);
        return result.ToArray();
    }
    internal static IReadOnlyList<Field> Read(byte[] bytes)
    {
        var result = new List<Field>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var tag = ReadVarint(bytes, ref offset);
            if (tag >> 3 is 0 or > 536870911) throw new InvalidDataException("Invalid protobuf field.");
            var wire = (int)(tag & 7);
            if (wire == 0) result.Add(new((int)(tag >> 3), wire, ReadVarint(bytes, ref offset), []));
            else
            {
                var size = wire switch { 1 => 8, 5 => 4, 2 => checked((int)ReadVarint(bytes, ref offset)),
                    _ => throw new InvalidDataException("Unsupported protobuf wire type.") };
                if (size < 0 || size > bytes.Length - offset) throw new InvalidDataException("Truncated protobuf field.");
                result.Add(new((int)(tag >> 3), wire, 0, bytes.AsSpan(offset, size).ToArray()));
                offset += size;
            }
        }
        return result;
    }
    private static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == bytes.Length) throw new InvalidDataException("Truncated varint.");
            var value = bytes[offset++];
            if (shift == 63 && value > 1) throw new InvalidDataException("Overflowing varint.");
            result |= (ulong)(value & 127) << shift;
            if ((value & 128) == 0) return result;
        }
        throw new InvalidDataException("Overflowing varint.");
    }
    internal static byte[] Data(this IReadOnlyList<Field> fields, int field) =>
        fields.LastOrDefault(f => f.Number == field && f.WireType == 2)?.Data ?? [];
    internal static ulong Number(this IReadOnlyList<Field> fields, int field, ulong fallback = 0) =>
        fields.LastOrDefault(f => f.Number == field && f.WireType == 0)?.Integer ?? fallback;
    internal static string String(this IReadOnlyList<Field> fields, int field) => Encoding.UTF8.GetString(fields.Data(field));
    internal static bool Has(this IReadOnlyList<Field> fields, int field) => fields.Any(f => f.Number == field);

    internal static byte[] Frame(byte[] body)
    {
        if (body.Length is 0 or > MaximumFrameSize) throw new InvalidDataException("Invalid Ubisoft frame size.");
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
    internal static byte[] Unframe(byte[] frame)
    {
        if (frame.Length < 4 || BinaryPrimitives.ReadInt32BigEndian(frame) != frame.Length - 4)
            throw new InvalidDataException("Invalid nested frame size.");
        return frame[4..];
    }
    internal static async Task<byte[]> ReadFrame(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaximumFrameSize) throw new InvalidDataException("Invalid Ubisoft frame size.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }
    internal static async Task WriteFrame(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Frame(body), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
