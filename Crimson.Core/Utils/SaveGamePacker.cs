using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using Crimson.Models;
using Ionic.Zlib;

namespace Crimson.Utils;

/// <summary>
/// Packages a local save directory into the chunk/manifest format Epic's savesync API
/// expects, mirroring legendary's SaveGameHelper, Chunk.write and Manifest.write.
/// Chunks are padded to a full 1 MiB window before hashing (Epic hashes the padded
/// window) and serialized as header version 3; the manifest is serialized as feature
/// level 18 with a compressed body. Packaged files are held in memory, which matches
/// the small size of typical save data.
/// </summary>
public static class SaveGamePacker
{
    private const int ChunkWindowSize = 1024 * 1024;
    private const uint ChunkHeaderMagic = 0xB1FE3AA2;
    private const int ChunkHeaderSize = 66; // version 3 header
    private const uint ManifestHeaderMagic = 0x44BEC00C;
    private const int ManifestHeaderSize = 41;
    private const int ManifestFeatureLevel = 18;
    public const string ManifestTimestampFormat = "yyyy.MM.dd-HH.mm.ss";

    /// <summary>
    /// Walks <paramref name="saveDirectory"/>, applies the cloud include/exclude filters and
    /// packs the matching files into chunks plus a manifest. Returns remote-relative paths
    /// (e.g. "manifests/…​.manifest", "ChunksV4/…​.chunk") mapped to their file contents,
    /// or an empty dictionary when nothing matches.
    /// </summary>
    public static Dictionary<string, byte[]> Package(
        string saveDirectory,
        string appName,
        string accountId,
        string cloudSaveFolder,
        IReadOnlyList<string>? includeFilters,
        IReadOnlyList<string>? excludeFilters,
        DateTimeOffset manifestTimestampUtc)
    {
        var files = EnumerateSaveFiles(saveDirectory, includeFilters, excludeFilters)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (files.Count == 0)
            return result;

        var chunks = new List<PackedChunk>();
        var fileManifests = new List<PackedFile>();
        var chunkBuffer = new MemoryStream();
        uint[]? currentGuid = null;

        foreach (var relativePath in files)
        {
            var fullPath = Path.Combine(saveDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            using var hasher = SHA1.Create();
            var file = new PackedFile { Filename = relativePath };
            using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long remaining = input.Length;

            while (remaining > 0)
            {
                if (currentGuid is null)
                {
                    currentGuid = NewGuid();
                    chunkBuffer.SetLength(0);
                    chunkBuffer.Position = 0;
                }

                var partSize = (int)Math.Min(remaining, ChunkWindowSize - chunkBuffer.Position);
                var part = new PackedChunkPart
                {
                    Guid = currentGuid,
                    Offset = (int)chunkBuffer.Position,
                    Size = partSize
                };
                CopyAndHash(input, chunkBuffer, hasher, partSize);
                file.ChunkParts.Add(part);
                remaining -= partSize;

                if (chunkBuffer.Position >= ChunkWindowSize)
                {
                    chunks.Add(FinalizeChunk(currentGuid, chunkBuffer));
                    currentGuid = null;
                }
            }

            hasher.TransformFinalBlock([], 0, 0);
            file.Hash = hasher.Hash!;
            fileManifests.Add(file);
        }

        if (currentGuid is not null)
            chunks.Add(FinalizeChunk(currentGuid, chunkBuffer));

        var buildVersion = manifestTimestampUtc.UtcDateTime.ToString(ManifestTimestampFormat, CultureInfo.InvariantCulture);
        var manifestBytes = WriteManifest(appName, accountId, buildVersion, cloudSaveFolder, chunks, fileManifests);
        foreach (var chunk in chunks)
            result[chunk.Info.Path] = chunk.Bytes;
        result[$"manifests/{buildVersion}.manifest"] = manifestBytes;
        return result;
    }

    /// <summary>
    /// Lists the files of a save directory that the cloud include/exclude filters cover.
    /// Mirrors legendary's matching: a trailing "/" makes the pattern a directory prefix,
    /// otherwise a plain suffix match or a '*'/'?' wildcard match applies.
    /// </summary>
    public static List<string> EnumerateSaveFiles(
        string saveDirectory,
        IReadOnlyList<string>? includeFilters,
        IReadOnlyList<string>? excludeFilters)
    {
        var matches = new List<string>();
        if (!Directory.Exists(saveDirectory))
            return matches;

        foreach (var path in Directory.EnumerateFiles(saveDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(saveDirectory, path).Replace('\\', '/');
            if (includeFilters is { Count: > 0 } && !MatchesAny(relative, includeFilters))
                continue;
            if (excludeFilters is { Count: > 0 } && MatchesAny(relative, excludeFilters))
                continue;
            matches.Add(relative);
        }

        return matches;
    }

    public static IReadOnlyList<string>? ParseFilters(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesAny(string filename, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            // Prefix/suffix branches are case-sensitive like legendary's str.startswith/endswith;
            // only the fnmatch-style wildcard branch is case-insensitive (normcase on Windows).
            if (pattern.EndsWith('/') && filename.StartsWith(pattern, StringComparison.Ordinal))
                return true;
            if (filename.EndsWith(pattern, StringComparison.Ordinal))
                return true;
            if (WildcardMatch(pattern, filename))
                return true;
        }

        return false;
    }

    // fnmatch-style glob supporting '*' and '?'; character classes are treated literally.
    private static bool WildcardMatch(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, anchor = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                anchor = v;
            }
            else if (star != -1)
            {
                p = star + 1;
                v = ++anchor;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static void CopyAndHash(Stream input, Stream output, HashAlgorithm hasher, int count)
    {
        var buffer = new byte[Math.Min(count, 81_920)];
        var remaining = count;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("Save file changed while it was being packaged.");
            output.Write(buffer, 0, read);
            hasher.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
    }

    private static PackedChunk FinalizeChunk(uint[] guid, MemoryStream buffer)
    {
        // Epic computes the chunk hashes over the payload padded to a full window.
        var padded = new byte[ChunkWindowSize];
        Array.Copy(buffer.GetBuffer(), padded, buffer.Position);
        var rollingHash = RollingHash.ComputeHash(padded);
        var shaHash = SHA1.HashData(padded);
        var compressed = ZlibCompress(padded);

        var info = new ChunkInfo(ManifestFeatureLevel)
        {
            Guid = guid.Select(u => unchecked((int)u)).ToArray(),
            Hash = unchecked((long)rollingHash),
            ShaHash = shaHash,
            WindowSize = ChunkWindowSize
        };

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ChunkHeaderMagic);
            writer.Write((uint)3); // header version
            writer.Write((uint)ChunkHeaderSize);
            writer.Write((uint)compressed.Length);
            foreach (var part in guid)
                writer.Write(part);
            writer.Write(rollingHash);
            writer.Write((byte)0x1); // stored compressed
            writer.Write(shaHash);
            writer.Write((byte)0x3); // rolling + sha hash
            writer.Write((uint)ChunkWindowSize);
            writer.Write(compressed);
        }

        var bytes = output.ToArray();
        info.FileSize = bytes.Length;
        return new PackedChunk { Info = info, Bytes = bytes };
    }

    private static byte[] WriteManifest(
        string appName,
        string accountId,
        string buildVersion,
        string cloudSaveFolder,
        List<PackedChunk> chunks,
        List<PackedFile> files)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            WriteManifestMeta(writer, appName, accountId, buildVersion);
            WriteChunkDataList(writer, chunks);
            WriteFileManifestList(writer, files);
            WriteCustomFields(writer, cloudSaveFolder);
        }

        var uncompressed = body.ToArray();
        var shaHash = SHA1.HashData(uncompressed);
        var compressed = ZlibCompress(uncompressed);

        using var envelope = new MemoryStream();
        using var header = new BinaryWriter(envelope, Encoding.UTF8, leaveOpen: true);
        header.Write(ManifestHeaderMagic);
        header.Write(ManifestHeaderSize);
        header.Write(uncompressed.Length);
        header.Write(compressed.Length);
        header.Write(shaHash);
        header.Write((byte)0x1); // stored compressed
        header.Write(ManifestFeatureLevel);
        header.Write(compressed);
        return envelope.ToArray();
    }

    private static void WriteManifestMeta(BinaryWriter writer, string appName, string accountId, string buildVersion)
    {
        var sizeOffset = writer.BaseStream.Position;
        writer.Write(0); // section size, backpatched
        writer.Write((byte)0); // data version
        writer.Write(ManifestFeatureLevel);
        writer.Write((byte)0); // is file data
        writer.Write(0); // app id
        WriteUnrealString(writer, appName + accountId);
        WriteUnrealString(writer, buildVersion);
        WriteUnrealString(writer, string.Empty); // launch exe
        WriteUnrealString(writer, string.Empty); // launch command
        writer.Write(0); // prereq id count
        WriteUnrealString(writer, string.Empty); // prereq name
        WriteUnrealString(writer, string.Empty); // prereq path
        WriteUnrealString(writer, string.Empty); // prereq args
        BackpatchSize(writer, sizeOffset);
    }

    private static void WriteChunkDataList(BinaryWriter writer, List<PackedChunk> chunks)
    {
        var sizeOffset = writer.BaseStream.Position;
        writer.Write(0); // section size, backpatched
        writer.Write((byte)0); // version
        writer.Write(chunks.Count);
        foreach (var chunk in chunks)
            foreach (var part in chunk.GuidParts)
                writer.Write(part);
        foreach (var chunk in chunks)
            writer.Write(unchecked((ulong)chunk.Info.Hash));
        foreach (var chunk in chunks)
            writer.Write(chunk.Info.ShaHash);
        foreach (var chunk in chunks)
            writer.Write((byte)chunk.Info.GroupNum);
        foreach (var chunk in chunks)
            writer.Write((uint)chunk.Info.WindowSize);
        foreach (var chunk in chunks)
            writer.Write(chunk.Info.FileSize);
        BackpatchSize(writer, sizeOffset);
    }

    private static void WriteFileManifestList(BinaryWriter writer, List<PackedFile> files)
    {
        var sizeOffset = writer.BaseStream.Position;
        writer.Write(0); // section size, backpatched
        writer.Write((byte)0); // version
        writer.Write(files.Count);
        foreach (var file in files)
            WriteUnrealString(writer, file.Filename);
        foreach (var file in files)
            WriteUnrealString(writer, string.Empty); // symlink target
        foreach (var file in files)
            writer.Write(file.Hash);
        foreach (var file in files)
            writer.Write((byte)0); // flags
        foreach (var file in files)
            writer.Write(0); // install tag count
        foreach (var file in files)
        {
            writer.Write(file.ChunkParts.Count);
            foreach (var part in file.ChunkParts)
            {
                writer.Write(28); // chunk part entry size
                foreach (var guidPart in part.Guid)
                    writer.Write(guidPart);
                writer.Write(part.Offset);
                writer.Write(part.Size);
            }
        }

        BackpatchSize(writer, sizeOffset);
    }

    private static void WriteCustomFields(BinaryWriter writer, string cloudSaveFolder)
    {
        var sizeOffset = writer.BaseStream.Position;
        writer.Write(0); // section size, backpatched
        writer.Write((byte)0); // version
        writer.Write(1); // field count
        WriteUnrealString(writer, "CloudSaveFolder");
        WriteUnrealString(writer, cloudSaveFolder);
        BackpatchSize(writer, sizeOffset);
    }

    private static void WriteUnrealString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write(0);
            return;
        }

        if (IsAscii(value))
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes.Length + 1);
            writer.Write(bytes);
            writer.Write((byte)0);
            return;
        }

        var utf16 = Encoding.Unicode.GetBytes(value);
        writer.Write(-(value.Length + 1));
        writer.Write(utf16);
        writer.Write((short)0);
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
            if (character > 0x7F)
                return false;
        return true;
    }

    private static void BackpatchSize(BinaryWriter writer, long sizeOffset)
    {
        var end = writer.BaseStream.Position;
        writer.BaseStream.Position = sizeOffset;
        writer.Write(checked((int)(end - sizeOffset)));
        writer.BaseStream.Position = end;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZlibStream(output, CompressionMode.Compress, true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static uint[] NewGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return
        [
            BitConverter.ToUInt32(bytes, 0),
            BitConverter.ToUInt32(bytes, 4),
            BitConverter.ToUInt32(bytes, 8),
            BitConverter.ToUInt32(bytes, 12)
        ];
    }

    private sealed class PackedChunk
    {
        public required ChunkInfo Info { get; init; }
        public required byte[] Bytes { get; init; }
        public IEnumerable<uint> GuidParts => Info.Guid.Select(i => unchecked((uint)i));
    }

    private sealed class PackedFile
    {
        public required string Filename { get; init; }
        public byte[] Hash { get; set; } = [];
        public List<PackedChunkPart> ChunkParts { get; } = [];
    }

    private sealed class PackedChunkPart
    {
        public required uint[] Guid { get; init; }
        public int Offset { get; init; }
        public int Size { get; init; }
    }
}
