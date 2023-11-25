using Ionic.Zlib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Crimson.Models;

public class Manifest
{
    private const uint HeaderMagic = 0x44BEC00C;
    private const int DefaultSerializationVersion = 17;

    public int HeaderSize { get; private set; } = 41;
    public int SizeCompressed { get; private set; } = 0;
    public int SizeUncompressed { get; private set; } = 0;
    public byte[] ShaHash { get; private set; } = Array.Empty<byte>();
    public byte StoredAs { get; private set; } = 0;
    public int Version { get; private set; } = 18;
    public byte[] Data { get; private set; } = Array.Empty<byte>();

    public bool Compressed => (StoredAs & 0x1) != 0;

    public static Manifest ReadAll(byte[] data)
    {
        var m = Read(data);
        using (var stream = new MemoryStream(m.Data))
        {
            var unhandledDataLength = stream.Length - stream.Position;
            if (unhandledDataLength > 0)
            {
                Console.WriteLine($"Did not read {unhandledDataLength} remaining bytes in manifest! This may not be a problem.");
            }
        }
        // Throw this away since the raw data is no longer needed
        m.Data = Array.Empty<byte>();

        return m;
    }

    public static Manifest Read(byte[] data)
    {
        using var bio = new MemoryStream(data);
        if (BitConverter.ToUInt32(ReadBytes(bio, 4), 0) != HeaderMagic)
        {
            throw new InvalidOperationException("No header magic!");
        }

        var manifest = new Manifest
        {
            HeaderSize = BitConverter.ToInt32(ReadBytes(bio, 4), 0),
            SizeUncompressed = BitConverter.ToInt32(ReadBytes(bio, 4), 0),
            SizeCompressed = BitConverter.ToInt32(ReadBytes(bio, 4), 0),
            ShaHash = ReadBytes(bio, 20),
            StoredAs = ReadBytes(bio, 1)[0],
            Version = BitConverter.ToInt32(ReadBytes(bio, 4), 0)
        };

        if (bio.Position != manifest.HeaderSize)
        {
            Console.WriteLine($"Did not read entire header {bio.Position} != {manifest.HeaderSize}! Header version: {manifest.Version}");
            bio.Seek(manifest.HeaderSize, SeekOrigin.Begin);
        }

        var rawData = ReadBytes(bio, (int)(bio.Length - bio.Position));
        if (manifest.Compressed)
        {
            manifest.Data = Decompress(rawData);
            var decHash = BitConverter.ToString(SHA1.HashData(manifest.Data)).Replace("-", string.Empty);
            var hexShaHash = BitConverter.ToString(manifest.ShaHash).Replace("-", string.Empty);

            if (decHash != hexShaHash)
            {
                throw new InvalidOperationException("Hash does not match!");
            }
        }
        else
        {
            manifest.Data = rawData;
        }

        return manifest;
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.Read(buffer, 0, count);
        return buffer;
    }

    private static void WriteBytes(Stream stream, byte[] data)
    {
        stream.Write(data, 0, data.Length);
    }

    private static byte[] Decompress(byte[] data)
    {
        using var compressedStream = new MemoryStream(data);
        using var decompressedStream = new MemoryStream();
        using (var zlibStream = new ZlibStream(compressedStream, CompressionMode.Decompress))
        {
            zlibStream.CopyTo(decompressedStream);
        }
        return decompressedStream.ToArray();
    }
}
