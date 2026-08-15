using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ionic.Zlib;

namespace Crimson.Models;

public sealed class Manifest
{
    private const uint HeaderMagic = 0x44BEC00C;
    private const int ExpectedHeaderSize = 41;
    private const int MinimumSupportedVersion = 17;
    private const int MaximumSupportedVersion = 21;

    public int HeaderSize { get; private set; } = ExpectedHeaderSize;
    public int SizeCompressed { get; private set; }
    public int SizeUncompressed { get; private set; }
    public byte[] ShaHash { get; private set; } = [];
    public byte StoredAs { get; private set; }
    public int Version { get; private set; } = MaximumSupportedVersion;
    public byte[] Data { get; private set; } = [];

    public ManifestMeta ManifestMeta { get; internal set; } = null!;
    public CDL CDL { get; internal set; } = null!;
    public FileManifestList FileManifestList { get; internal set; } = null!;
    public CustomFields CustomFields { get; internal set; } = null!;

    public bool Compressed => (StoredAs & 0x1) != 0;

    public static Manifest ReadAll(byte[] data)
    {
        if (JsonManifestReader.IsJson(data))
            return JsonManifestReader.Read(data);
        var manifest = Read(data);
        using var stream = new MemoryStream(manifest.Data, writable: false);
        var reader = new EpicBinaryReader(stream);
        manifest.ManifestMeta = ManifestMeta.Read(reader);
        manifest.CDL = CDL.Read(reader, checked((int)manifest.ManifestMeta.FeatureLevel));
        manifest.FileManifestList = FileManifestList.Read(reader);
        manifest.CustomFields = CustomFields.Read(reader);
        reader.EnsureAtEnd("Manifest payload");
        manifest.Data = [];
        return manifest;
    }

    public static Manifest Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (JsonManifestReader.IsJson(data))
            return JsonManifestReader.Read(data);
        if (data.Length < ExpectedHeaderSize)
            throw new EndOfStreamException("Manifest header is truncated.");
        if (data.Length > EpicProtocolLimits.MaximumManifestBytes + ExpectedHeaderSize)
            throw new InvalidDataException("Manifest exceeds the supported size limit.");

        using var stream = new MemoryStream(data, writable: false);
        var reader = new EpicBinaryReader(stream);
        if (reader.ReadUInt32() != HeaderMagic)
            throw new InvalidDataException("Manifest header magic is invalid.");

        var manifest = new Manifest
        {
            HeaderSize = reader.ReadInt32(),
            SizeUncompressed = reader.ReadInt32(),
            SizeCompressed = reader.ReadInt32(),
            ShaHash = reader.ReadBytesExact(20),
            StoredAs = reader.ReadByte(),
            Version = reader.ReadInt32()
        };

        if (manifest.HeaderSize != ExpectedHeaderSize || reader.Position != ExpectedHeaderSize)
            throw new InvalidDataException("Manifest header size is unsupported.");
        if (manifest.Version is < MinimumSupportedVersion or > MaximumSupportedVersion)
            throw new InvalidDataException($"Manifest version {manifest.Version} is unsupported.");
        if ((manifest.StoredAs & ~0x1) != 0)
            throw new InvalidDataException("Manifest storage flags are unsupported.");
        ValidateSize(manifest.SizeCompressed, "compressed");
        ValidateSize(manifest.SizeUncompressed, "uncompressed");
        if (reader.Remaining != manifest.SizeCompressed)
            throw new InvalidDataException("Manifest compressed size does not match its payload.");
        if (manifest.Compressed && manifest.SizeUncompressed >
            Math.Max((long)manifest.SizeCompressed, 1) * EpicProtocolLimits.MaximumManifestDecompressionRatio)
            throw new InvalidDataException("Manifest decompression ratio exceeds the supported limit.");

        var storedPayload = reader.ReadBytesExact(manifest.SizeCompressed);
        reader.EnsureAtEnd("Manifest envelope");
        manifest.Data = manifest.Compressed
            ? Decompress(storedPayload, manifest.SizeUncompressed)
            : storedPayload;
        if (manifest.Data.Length != manifest.SizeUncompressed)
            throw new InvalidDataException("Manifest uncompressed size does not match its payload.");
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(manifest.Data), manifest.ShaHash))
            throw new InvalidDataException("Manifest payload hash does not match.");

        return manifest;
    }

    private static void ValidateSize(int size, string name)
    {
        if (size < 0 || size > EpicProtocolLimits.MaximumManifestBytes)
            throw new InvalidDataException($"Manifest {name} size is outside the supported range.");
    }

    private static byte[] Decompress(byte[] data, int expectedSize)
    {
        using var compressedStream = new MemoryStream(data, writable: false);
        using var zlibStream = new ZlibStream(compressedStream, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedSize);
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            var total = 0;
            while (true)
            {
                var read = zlibStream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                total = checked(total + read);
                if (total > expectedSize || total > EpicProtocolLimits.MaximumManifestBytes)
                    throw new InvalidDataException("Manifest decompressed beyond its declared size.");

                output.Write(buffer, 0, read);
            }

            if (total != expectedSize)
                throw new InvalidDataException("Manifest decompressed size does not match its declaration.");

            return output.ToArray();
        }
        catch (ZlibException exception)
        {
            throw new InvalidDataException("Manifest compressed payload is invalid.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Manifest decompressed size overflowed.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed class ManifestMeta
{
    public int MetaSize { get; internal set; }
    public byte DataVersion { get; internal set; }
    public uint FeatureLevel { get; internal set; }
    public bool IsFileData { get; internal set; }
    public uint AppId { get; internal set; }
    public string AppName { get; internal set; } = string.Empty;
    public string BuildVersion { get; internal set; } = string.Empty;
    public string LaunchExe { get; internal set; } = string.Empty;
    public string LaunchCommand { get; internal set; } = string.Empty;
    public List<string> PrereqIds { get; internal set; } = [];
    public string PrereqName { get; internal set; } = string.Empty;
    public string PrereqPath { get; internal set; } = string.Empty;
    public string PrereqArgs { get; internal set; } = string.Empty;
    public string UninstallActionPath { get; internal set; } = string.Empty;
    public string UninstallActionArgs { get; internal set; } = string.Empty;

    private string? _buildId;

    public string BuildId
    {
        get
        {
            if (!string.IsNullOrEmpty(_buildId))
                return _buildId;

            using var input = new MemoryStream();
            using (var writer = new BinaryWriter(input, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(AppId);
                writer.Write(Encoding.UTF8.GetBytes(AppName));
                writer.Write(Encoding.UTF8.GetBytes(BuildVersion));
                writer.Write(Encoding.UTF8.GetBytes(LaunchExe));
                writer.Write(Encoding.UTF8.GetBytes(LaunchCommand));
            }

            _buildId = Convert.ToBase64String(SHA1.HashData(input.ToArray()))
                .Replace("+", "-", StringComparison.Ordinal)
                .Replace("/", "_", StringComparison.Ordinal)
                .Replace("=", string.Empty, StringComparison.Ordinal);
            return _buildId;
        }
    }

    internal static ManifestMeta Read(EpicBinaryReader reader)
    {
        var start = reader.Position;
        var end = reader.BeginSection("Manifest metadata", minimumSize: 15);
        var meta = new ManifestMeta
        {
            MetaSize = checked((int)(end - start)),
            DataVersion = reader.ReadByte(),
            FeatureLevel = reader.ReadUInt32()
        };
        if (meta.DataVersion > 2)
            throw new InvalidDataException($"Manifest metadata version {meta.DataVersion} is unsupported.");
        if (meta.FeatureLevel > 21)
            throw new InvalidDataException($"Manifest feature level {meta.FeatureLevel} is unsupported.");

        var isFileData = reader.ReadByte();
        if (isFileData > 1)
            throw new InvalidDataException("Manifest file-data flag is invalid.");

        meta.IsFileData = isFileData == 1;
        meta.AppId = reader.ReadUInt32();
        meta.AppName = reader.ReadUnrealString();
        meta.BuildVersion = reader.ReadUnrealString();
        meta.LaunchExe = reader.ReadUnrealString(EpicProtocolLimits.MaximumPathBytes);
        meta.LaunchCommand = reader.ReadUnrealString();

        var prereqCount = reader.ReadUInt32();
        if (prereqCount > 4_096)
            throw new InvalidDataException("Manifest prerequisite count exceeds the supported limit.");
        for (var index = 0; index < prereqCount; index++)
            meta.PrereqIds.Add(reader.ReadUnrealString());

        meta.PrereqName = reader.ReadUnrealString();
        meta.PrereqPath = reader.ReadUnrealString(EpicProtocolLimits.MaximumPathBytes);
        meta.PrereqArgs = reader.ReadUnrealString();
        if (meta.DataVersion >= 1)
            meta._buildId = reader.ReadUnrealString();
        if (meta.DataVersion >= 2)
        {
            meta.UninstallActionPath = reader.ReadUnrealString(EpicProtocolLimits.MaximumPathBytes);
            meta.UninstallActionArgs = reader.ReadUnrealString();
        }

        reader.EndSection(end, "Manifest metadata");
        return meta;
    }
}
