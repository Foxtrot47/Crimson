using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Crimson.Utils;
using Ionic.Zlib;

namespace Crimson.Models;

public class Chunk
{
    private const uint HeaderMagic = 0xB1FE3AA2;
    private uint[] _guid;
    private byte[] _data;
    private int? _groupNum;
    private string _guidStr;
    private BigInteger _guidNum;
    private MemoryStream _bio;

    public uint HeaderVersion { get; private set; } = 3;
    public uint HeaderSize { get; private set; } = 0;
    public uint CompressedSize { get; private set; } = 0;
    public ulong Hash { get; private set; } = 0;
    public byte StoredAs { get; private set; } = 0;
    public byte HashType { get; private set; } = 0;
    public byte[] ShaHash { get; private set; }
    public uint UncompressedSize { get; private set; } = 1024 * 1024;

    public bool Compressed => (StoredAs & 0x1) != 0;

    public Chunk()
    {
        _guid = GenerateGuid();
        ShaHash = new byte[20];
        _bio = new MemoryStream();
    }

    private static uint[] GenerateGuid()
    {
        var guid = System.Guid.NewGuid();
        var bytes = guid.ToByteArray();

        // Unpacking the GUID into four 32-bit integers
        return new[]
        {
            BitConverter.ToUInt32(bytes, 0),
            BitConverter.ToUInt32(bytes, 4),
            BitConverter.ToUInt32(bytes, 8),
            BitConverter.ToUInt32(bytes, 12)
        };
    }
    public string GuidStr
    {
        get
        {
            if (_guidStr == null && _guid != null) _guidStr = string.Join("-", _guid.Select(g => g.ToString("x8")));
            return _guidStr;
        }
    }

    public BigInteger GuidNum
    {
        get
        {
            if (_guidNum == -1 && _guid != null)
                _guidNum = new BigInteger(_guid[3])
                           + (new BigInteger(_guid[2]) << 32)
                           + (new BigInteger(_guid[1]) << 64)
                           + (new BigInteger(_guid[0]) << 96);

            return _guidNum;
        }
    }

    public byte[] Data
    {
        get
        {
            if (_data != null)
                return _data;

            if (_bio != null)
            {

                _bio.Position = 0; // Reset stream position
                if (Compressed)
                {
                    using (var deflateStream = new ZlibStream(_bio, CompressionMode.Decompress))
                    using (var resultStream = new MemoryStream())
                    {
                        deflateStream.CopyTo(resultStream);
                        _data = resultStream.ToArray();
                    }
                }
                else
                {
                    _data = _bio.ToArray();
                }

                _bio.Dispose();
                _bio = null;
            }

            return _data;
        }
        set
        {
            if (value.Length > 1024 * 1024)
                throw new ArgumentException("Data too large (> 1 MiB)");

            if (Compressed)
                StoredAs ^= 0x1; // Toggle the compressed flag

            var paddedValue = new byte[1024 * 1024];
            Array.Copy(value, paddedValue, value.Length);

            // Recalculate hashes
            Hash = RollingHash.ComputeHash(paddedValue);
            using (var sha1 = SHA1.Create())
            {
                ShaHash = sha1.ComputeHash(paddedValue);
            }

            HashType = 0x3; // Indicate both rolling hash and SHA1 hash are used
            _data = paddedValue;
        }
    }


    public static Chunk ReadBuffer(byte[] data)
    {
        using (var ms = new MemoryStream(data))
        using (var reader = new BinaryReader(ms))
        {
            return Read(reader);
        }
    }

    public static Chunk Read(BinaryReader reader)
    {
        var headStart = reader.BaseStream.Position;

        if (reader.ReadUInt32() != HeaderMagic)
            throw new InvalidOperationException("Chunk magic doesn't match!");

        var chunk = new Chunk
        {
            HeaderVersion = reader.ReadUInt32(),
            HeaderSize = reader.ReadUInt32(),
            CompressedSize = reader.ReadUInt32(),
            _guid = ReadGuid(reader),
            Hash = reader.ReadUInt64(),
            StoredAs = reader.ReadByte()
        };

        if (chunk.HeaderVersion >= 2)
        {
            chunk.ShaHash = reader.ReadBytes(20);
            chunk.HashType = reader.ReadByte();
        }

        if (chunk.HeaderVersion >= 3)
        {
            chunk.UncompressedSize = reader.ReadUInt32();
        }

        if (reader.BaseStream.Position - headStart != chunk.HeaderSize)
            throw new InvalidOperationException("Did not read entire chunk header!");

        chunk._bio = new MemoryStream(reader.ReadBytes((int)(chunk.CompressedSize)));
        return chunk;
    }

    private static uint[] ReadGuid(BinaryReader reader)
    {
        return new uint[]
        {
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32()
        };
    }

    public byte[] WriteToBuffer()
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            // Write data to MemoryStream
            return ms.ToArray();
        }
    }
}