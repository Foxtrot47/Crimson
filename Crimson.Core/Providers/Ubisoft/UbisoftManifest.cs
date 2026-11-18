using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Crimson.Providers.Ubisoft.Protocol;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft;

public enum UbisoftCompression { None = 0, Deflate = 1, Lzham = 2, Zstd = 3 }

/// <summary>
/// One CDN object of a file. <paramref name="Size"/> is the decoded length, <paramref name="DownloadSize"/> the
/// object's length, <paramref name="DownloadSha1"/> both its CDN name and the SHA-1 of the downloaded bytes,
/// <paramref name="ContentSha1"/> the SHA-1 of the decoded bytes (null when the manifest does not carry it),
/// and <paramref name="Offset"/> where the decoded bytes go in the file.
/// </summary>
public sealed record UbisoftSlice(uint Size, uint DownloadSize, string DownloadSha1, byte[]? ContentSha1, ulong Offset);

/// <summary>A manifest file entry. <paramref name="Path"/> is relative to the install root, backslash separated.</summary>
public sealed record UbisoftManifestFile(string Path, ulong Size, bool IsDirectory, IReadOnlyList<UbisoftSlice> Slices);

public sealed record UbisoftManifestChunk(uint Id, bool Optional, string? Language, string? Tags, IReadOnlyList<uint> UplayIds, IReadOnlyList<UbisoftManifestFile> Files);

/// <summary>A prerequisite installer; paths and arguments may contain <c>$INSTALL_DIR</c>.</summary>
public sealed record UbisoftInstallRun(string Exe, string WorkingDirectory, string Arguments, string Description, string? Identifier, uint Version, uint? Platform, uint? PlatformType, bool IgnoreAllExitCodes, bool RestartRequired);

/// <summary>One value of an install registry entry; a non-null <paramref name="Language"/> applies only to that install language.</summary>
public sealed record UbisoftRegistryValue(string Value, string? Language);

/// <summary>A registry value the official client writes. <paramref name="Key"/> is the full path including the value name.</summary>
public sealed record UbisoftInstallRegistry(string Key, bool IsNumber, IReadOnlyList<UbisoftRegistryValue> Values);

public sealed record UbisoftUninstallRun(string Exe, string WorkingDirectory, string Arguments);

/// <summary>An install language; non-empty <paramref name="UplayIds"/> means one of those products must be owned.</summary>
public sealed record UbisoftManifestLanguage(string Code, IReadOnlyList<uint> UplayIds);

/// <summary>
/// The <c>.manifest</c> of one build (<c>mg.protocol.download.Manifest</c>). The container is
/// <c>u32 version | u32 signature length | u32 body length | signature | zlib(body)</c>, little endian.
/// </summary>
public sealed class UbisoftManifest
{
    internal const int HeaderLength = 12;
    internal const int MaximumBodyLength = 256 * 1024 * 1024;
    private const int Sha1Length = 20;

    /// <summary>base32hex: the CDN bucket is the first hash byte folded to 32 buckets, not the first hex digit.</summary>
    private const string ShardAlphabet = "0123456789abcdefghijklmnopqrstuv";

    private UbisoftManifest() { }

    public IReadOnlyList<UbisoftManifestChunk> Chunks { get; private init; } = [];
    public IReadOnlyList<UbisoftInstallRun> InstallRuns { get; private init; } = [];
    public IReadOnlyList<UbisoftInstallRegistry> InstallRegistry { get; private init; } = [];
    public IReadOnlyList<string> UninstallRegistry { get; private init; } = [];
    public IReadOnlyList<UbisoftUninstallRun> UninstallRuns { get; private init; } = [];
    public IReadOnlyList<UbisoftManifestLanguage> Languages { get; private init; } = [];
    public UbisoftCompression Compression { get; private init; }
    public bool IsCompressed { get; private init; }
    public string? GameVersion { get; private init; }
    public uint Version { get; private init; }

    public static UbisoftManifest Parse(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var body = Read(Inflate(container));
        var compression = body.Number(21);
        if (compression > (ulong)UbisoftCompression.Zstd)
            throw new InvalidDataException($"Ubisoft manifest declares unknown compression {compression}.");
        return new UbisoftManifest
        {
            Chunks = Repeated(body, 4).Select(ParseChunk).ToArray(),
            InstallRuns = Repeated(body, 2).Select(ParseInstallRun).ToArray(),
            InstallRegistry = Repeated(body, 5).Select(ParseInstallRegistry).ToArray(),
            UninstallRegistry = Repeated(body, 6).Select(entry => Text(entry, 1)).Where(key => key.Length > 0).ToArray(),
            UninstallRuns = Repeated(body, 3).Select(run => new UbisoftUninstallRun(Text(run, 1), Text(run, 2), Text(run, 3))).ToArray(),
            Languages = ParseLanguages(body, 23),
            Compression = (UbisoftCompression)compression,
            IsCompressed = body.Number(17) != 0,
            GameVersion = OptionalText(body, 20),
            Version = UInt32(body, 22),
        };
    }

    public static string SlicePath(string downloadSha1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadSha1);
        var hash = downloadSha1.Trim().ToUpperInvariant();
        if (hash.Length != Sha1Length * 2 || !hash.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A slice hash is 40 hex characters.", nameof(downloadSha1));
        var first = byte.Parse(hash.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"slices_v3/{ShardAlphabet[first % ShardAlphabet.Length]}/{hash}");
    }

    /// <summary>
    /// The manifest ID published in ownership is the SHA-1 of the compressed body, not of the whole container.
    /// Verify it before parsing a cached or downloaded manifest.
    /// </summary>
    public static string ComputeId(byte[] container)
    {
        var (offset, length) = BodyRange(container);
        return Convert.ToHexString(SHA1.HashData(container.AsSpan(offset, length)));
    }

    private static (int Offset, int Length) BodyRange(byte[] container)
    {
        if (container.Length < HeaderLength) throw new InvalidDataException("Ubisoft manifest container is truncated.");
        var signatureLength = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(4));
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8));
        if ((ulong)HeaderLength + signatureLength + bodyLength > (ulong)container.Length)
            throw new InvalidDataException("Ubisoft manifest container is truncated.");
        if (bodyLength == 0) throw new InvalidDataException("Ubisoft manifest container has no body.");
        return (HeaderLength + (int)signatureLength, (int)bodyLength);
    }

    internal static byte[] Inflate(byte[] container, int maximumLength = MaximumBodyLength)
    {
        var (offset, length) = BodyRange(container);
        using var source = new MemoryStream(container, offset, length, writable: false);
        using var inflated = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (inflated.Length + read > maximumLength)
                    throw new InvalidDataException("Ubisoft manifest body exceeds the size limit.");
                inflated.Write(buffer, 0, read);
            }
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("Ubisoft manifest body is not a valid zlib stream.", exception);
        }
        return inflated.ToArray();
    }

    /// <summary>A repeated uint32, accepting both the unpacked (proto2 default) and packed encodings.</summary>
    internal static IReadOnlyList<uint> UInt32s(IReadOnlyList<Field> fields, int number)
    {
        var values = new List<uint>();
        foreach (var field in fields)
        {
            if (field.Number != number) continue;
            if (field.WireType == 0) values.Add(checked((uint)field.Integer));
            else if (field.WireType == 2)
            {
                var offset = 0;
                while (offset < field.Data.Length) values.Add(checked((uint)Varint(field.Data, ref offset)));
            }
        }
        return values;
    }

    internal static IEnumerable<IReadOnlyList<Field>> Repeated(IReadOnlyList<Field> fields, int number) =>
        fields.Where(field => field.Number == number && field.WireType == 2).Select(field => UbisoftWire.Read(field.Data));

    internal static IReadOnlyList<UbisoftManifestLanguage> ParseLanguages(IReadOnlyList<Field> fields, int number) =>
        Repeated(fields, number)
            .Select(language => new UbisoftManifestLanguage(Text(language, 1), UInt32s(language, 2)))
            .Where(language => language.Code.Length > 0)
            .ToArray();

    internal static string Text(IReadOnlyList<Field> fields, int number) => fields.String(number);

    internal static string? OptionalText(IReadOnlyList<Field> fields, int number)
    {
        var value = fields.String(number);
        return value.Length == 0 ? null : value;
    }

    internal static uint UInt32(IReadOnlyList<Field> fields, int number) => checked((uint)fields.Number(number));

    private static UbisoftManifestChunk ParseChunk(IReadOnlyList<Field> chunk)
    {
        var uplayIds = UInt32s(chunk, 8);
        // Older manifests gate a chunk with the single uplayId field instead of the list.
        if (uplayIds.Count == 0 && chunk.Number(4) is var single and > 0) uplayIds = [checked((uint)single)];
        return new UbisoftManifestChunk(
            UInt32(chunk, 1),
            chunk.Number(2) == 1,
            OptionalText(chunk, 5),
            OptionalText(chunk, 7),
            uplayIds,
            Repeated(chunk, 3).Select(ParseFile).ToArray());
    }

    private static UbisoftManifestFile ParseFile(IReadOnlyList<Field> file)
    {
        var path = NormalizePath(Text(file, 1));
        var size = file.Number(2);
        var isDirectory = file.Number(3) != 0;
        var contentHashes = file.Where(field => field.Number == 4 && field.WireType == 2).Select(field => field.Data).ToArray();
        var sliceList = Repeated(file, 7).ToArray();
        if (isDirectory || size == 0)
            return new UbisoftManifestFile(path, size, isDirectory, []);
        if (sliceList.Length == 0)
            throw new InvalidDataException($"Ubisoft manifest file '{path}' has no slice list.");

        var withContentHashes = contentHashes.Length == sliceList.Length && contentHashes.All(hash => hash.Length == Sha1Length);
        return new UbisoftManifestFile(path, size, false, ParseSlices(path, size, sliceList, withContentHashes ? contentHashes : null));
    }

    private static UbisoftSlice[] ParseSlices(string path, ulong size, IReadOnlyList<Field>[] sliceList, byte[][]? contentHashes)
    {
        var slices = new UbisoftSlice[sliceList.Length];
        ulong next = 0;
        for (var index = 0; index < sliceList.Length; index++)
        {
            var slice = sliceList[index];
            var downloadSha1 = slice.Data(3);
            if (downloadSha1.Length != Sha1Length)
                throw new InvalidDataException($"Ubisoft manifest file '{path}' has a slice without a valid hash.");
            var sliceSize = UInt32(slice, 1);
            var offset = slice.Has(4) ? slice.Number(4) : next;
            if (offset > size || sliceSize > size - offset)
                throw new InvalidDataException($"Ubisoft manifest file '{path}' has a slice beyond its size.");
            slices[index] = new UbisoftSlice(sliceSize, UInt32(slice, 2), Convert.ToHexString(downloadSha1), contentHashes?[index], offset);
            next = offset + sliceSize;
        }
        return slices;
    }

    private static UbisoftInstallRun ParseInstallRun(IReadOnlyList<Field> run) => new(
        Text(run, 1), Text(run, 2), Text(run, 3), Text(run, 4), OptionalText(run, 5), UInt32(run, 6),
        run.Has(7) ? UInt32(run, 7) : null, run.Has(8) ? UInt32(run, 8) : null,
        run.Number(9) != 0, run.Number(10) != 0);

    private static UbisoftInstallRegistry ParseInstallRegistry(IReadOnlyList<Field> entry)
    {
        var isNumber = entry.Number(2) == 1;
        var values = isNumber
            ? Repeated(entry, 4).Select(value => new UbisoftRegistryValue(
                UInt32(value, 1).ToString(CultureInfo.InvariantCulture), OptionalText(value, 2)))
            : Repeated(entry, 3).Select(value => new UbisoftRegistryValue(Text(value, 1), OptionalText(value, 2)));
        return new UbisoftInstallRegistry(Text(entry, 1), isNumber, values.ToArray());
    }

    /// <summary>
    /// Manifest paths are Windows paths, sometimes with a leading separator. Keep them relative and refuse anything
    /// that could leave the install root once joined (<c>..</c>, drive letters, alternate data streams).
    /// </summary>
    internal static string NormalizePath(string path)
    {
        var segments = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidDataException("Ubisoft manifest has a file without a name.");
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':') || segment.Any(char.IsControl))
                throw new InvalidDataException($"Ubisoft manifest has an unsafe file path '{path}'.");
        }
        return string.Join('\\', segments);
    }

    private static ulong Varint(byte[] bytes, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated packed varint.");
            var value = bytes[offset++];
            result |= (ulong)(value & 127) << shift;
            if ((value & 128) == 0) return result;
        }
        throw new InvalidDataException("Overflowing packed varint.");
    }
}
