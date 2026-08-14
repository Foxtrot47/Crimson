using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ionic.Zlib;

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

    public ManifestMeta ManifestMeta { get; set; }
    public CDL CDL { get; set; }
    public FileManifestList FileManifestList { get; set; }
    public CustomFields CustomFields { get; set; }

    public bool Compressed => (StoredAs & 0x1) != 0;

    public static Manifest ReadAll(byte[] data)
    {
        var manifest = Read(data);
        using (var stream = new MemoryStream(manifest.Data))
        {
            manifest.ManifestMeta = ManifestMeta.Read(new BinaryReader(stream));
            manifest.CDL = CDL.Read(stream, Convert.ToInt32(manifest.ManifestMeta.FeatureLevel));
            manifest.FileManifestList = FileManifestList.Read(stream);
            manifest.CustomFields = CustomFields.Read(stream);
            var unhandledDataLength = stream.Length - stream.Position;
            if (unhandledDataLength > 0)
                Console.WriteLine(
                    $"Did not read {unhandledDataLength} remaining bytes in manifest! This may not be a problem.");
        }

        // Throw this away since the raw data is no longer needed
        manifest.Data = Array.Empty<byte>();

        return manifest;
    }

    public static Manifest Read(byte[] data)
    {
        using var bio = new MemoryStream(data);
        if (BitConverter.ToUInt32(ReadBytes(bio, 4), 0) != HeaderMagic)
            throw new InvalidOperationException("No header magic!");

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
            Console.WriteLine(
                $"Did not read entire header {bio.Position} != {manifest.HeaderSize}! Header version: {manifest.Version}");
            bio.Seek(manifest.HeaderSize, SeekOrigin.Begin);
        }

        var rawData = ReadBytes(bio, (int)(bio.Length - bio.Position));
        if (manifest.Compressed)
        {
            manifest.Data = Decompress(rawData);
            var decHash = BitConverter.ToString(SHA1.HashData(manifest.Data)).Replace("-", string.Empty);
            var hexShaHash = BitConverter.ToString(manifest.ShaHash).Replace("-", string.Empty);

            if (decHash != hexShaHash) throw new InvalidOperationException("Hash does not match!");
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

public class ManifestMeta
{
    public int MetaSize { get; private set; }
    public byte DataVersion { get; private set; }
    public uint FeatureLevel { get; private set; }
    public bool IsFileData { get; private set; }
    public uint AppId { get; private set; }
    public string AppName { get; private set; }
    public string BuildVersion { get; private set; }
    public string LaunchExe { get; private set; }
    public string LaunchCommand { get; private set; }
    public List<string> PrereqIds { get; private set; }
    public string PrereqName { get; private set; }
    public string PrereqPath { get; private set; }
    public string PrereqArgs { get; private set; }
    public string UninstallActionPath { get; private set; }
    public string UninstallActionArgs { get; private set; }

    private string _buildId;

    public string BuildId
    {
        get
        {
            if (!string.IsNullOrEmpty(_buildId)) return _buildId;

            using (var sha1 = new SHA1Managed())
            {
                var hashBytes = sha1.ComputeHash(BitConverter.GetBytes(AppId));
                hashBytes = CombineArrays(hashBytes, Encoding.UTF8.GetBytes(AppName));
                hashBytes = CombineArrays(hashBytes, Encoding.UTF8.GetBytes(BuildVersion));
                hashBytes = CombineArrays(hashBytes, Encoding.UTF8.GetBytes(LaunchExe));
                hashBytes = CombineArrays(hashBytes, Encoding.UTF8.GetBytes(LaunchCommand));

                _buildId = Convert.ToBase64String(hashBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
            }

            return _buildId;
        }
    }

    public static ManifestMeta Read(BinaryReader reader)
    {
        var meta = new ManifestMeta
        {
            MetaSize = reader.ReadInt32(),
            DataVersion = reader.ReadByte(),
            FeatureLevel = reader.ReadUInt32(),
            IsFileData = reader.ReadByte() == 1,
            AppId = reader.ReadUInt32(),
            AppName = ReadFString(reader),
            BuildVersion = ReadFString(reader),
            LaunchExe = ReadFString(reader),
            LaunchCommand = ReadFString(reader)
        };

        var entries = reader.ReadUInt32();
        meta.PrereqIds = new List<string>();
        for (var i = 0; i < entries; i++) meta.PrereqIds.Add(ReadFString(reader));

        meta.PrereqName = ReadFString(reader);
        meta.PrereqPath = ReadFString(reader);
        meta.PrereqArgs = ReadFString(reader);

        if (meta.DataVersion >= 1) meta._buildId = ReadFString(reader);

        if (meta.DataVersion >= 2)
        {
            meta.UninstallActionPath = ReadFString(reader);
            meta.UninstallActionArgs = ReadFString(reader);
        }

        var sizeRead = (int)reader.BaseStream.Position;
        if (sizeRead == meta.MetaSize) return meta;

        Console.WriteLine($"Did not read entire manifest metadata! Version: {meta.DataVersion}, " +
                          $"{meta.MetaSize - sizeRead} bytes missing, skipping...");
        reader.BaseStream.Seek(meta.MetaSize - sizeRead, SeekOrigin.Current);
        // Downgrade version to prevent issues during serialization
        meta.DataVersion = 0;

        return meta;
    }

    private static byte[] CombineArrays(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static string ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        switch (length)
        {
            case < 0:
                {
                    length *= -2;
                    var utf16Bytes = reader.ReadBytes(length - 2);
                    reader.BaseStream.Seek(2, SeekOrigin.Current); // UTF-16 strings have two-byte null terminators
                    return Encoding.Unicode.GetString(utf16Bytes);
                }
            case > 0:
                {
                    var asciiBytes = reader.ReadBytes(length - 1);
                    reader.BaseStream.Seek(1, SeekOrigin.Current); // Skip string null terminator
                    return Encoding.ASCII.GetString(asciiBytes);
                }
            default:
                return string.Empty;
        }
    }
}