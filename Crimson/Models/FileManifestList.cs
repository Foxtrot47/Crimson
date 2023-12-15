using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Crimson.Models;

public class FileManifestList
{
    public int Version { get; set; }
    public int Size { get; set; }
    public int Count { get; set; }
    public List<FileManifest> Elements { get; set; }
    private Dictionary<string, int> _pathMap;

    public FileManifestList()
    {
        Version = 0;
        Size = 0;
        Count = 0;
        Elements = new List<FileManifest>();
        _pathMap = new Dictionary<string, int>();
    }

    public FileManifest GetFileByPath(string path)
    {
        if (_pathMap.Count == 0)
            for (var i = 0; i < Elements.Count; i++)
                _pathMap[Elements[i].Filename] = i;

        if (_pathMap.TryGetValue(path, out var index))
            return Elements[index];
        else
            throw new ArgumentException($"Invalid path! {path}");
    }

    public static FileManifestList Read(Stream bio)
    {
        var fml = new FileManifestList();
        var reader = new BinaryReader(bio);

        var fmlStart = bio.Position;
        fml.Size = reader.ReadInt32();
        fml.Version = reader.ReadByte();
        fml.Count = reader.ReadInt32();

        for (var i = 0; i < fml.Count; i++)
        {
            var fm = new FileManifest
            {
                Filename = ReadFString(reader)
            };
            fml.Elements.Add(fm);
        }

        foreach (var fm in fml.Elements) fm.SymlinkTarget = ReadFString(reader);
        // For files this is actually the SHA1 instead of whatever it is for chunks...
        foreach (var fm in fml.Elements) fm.Hash = reader.ReadBytes(20);
        // Flags, the only one I've seen is for executables
        foreach (var fm in fml.Elements) fm.Flags = reader.ReadByte();

        foreach (var fm in fml.Elements)
        {
            var tagCount = reader.ReadInt32();
            for (var j = 0; j < tagCount; j++) fm.InstallTags.Add(ReadFString(reader));
        }

        // Each file is made up of "Chunk Parts" that can be spread across the "chunk stream"
        foreach (var fm in fml.Elements)
        {
            var partCount = reader.ReadInt32();
            var offset = 0;
            for (var j = 0; j < partCount; j++)
            {
                var chunkPartStart = bio.Position;
                var chunkPartSize = reader.ReadInt32();
                var chunkPart = new ChunkPart
                {
                    Guid = new int[4]
                };
                for (var k = 0; k < 4; k++) chunkPart.Guid[k] = reader.ReadInt32();

                chunkPart.Offset = reader.ReadInt32();
                chunkPart.Size = reader.ReadInt32();
                chunkPart.FileOffset = offset;
                fm.ChunkParts.Add(chunkPart);
                offset += chunkPart.Size;

                // Skipping unread bytes if any
                var diff = bio.Position - chunkPartStart - chunkPartSize;
                if (diff > 0) bio.Seek(diff, SeekOrigin.Current);
            }
        }

        // MD5 hash + MIME type (Manifest feature level 19)
        if (fml.Version >= 1)
        {
            foreach (var fm in fml.Elements)
            {
                var hasMd5 = reader.ReadInt32();
                if (hasMd5 != 0) fm.HashMd5 = reader.ReadBytes(16);
            }

            foreach (var fm in fml.Elements) fm.MimeType = ReadFString(reader);
        }

        // SHA256 hash (Manifest feature level 20)
        if (fml.Version >= 2)
            foreach (var fm in fml.Elements)
                fm.HashSha256 = reader.ReadBytes(32);

        // Calculate file size ourselves
        foreach (var fm in fml.Elements) fm.FileSize = fm.ChunkParts.Sum(c => (long)c.Size);

        var sizeRead = bio.Position - fmlStart;
        if (sizeRead != fml.Size)
        {
            // Log warning here and seek forward
            fml.Version = 0;
            bio.Seek(fml.Size - sizeRead, SeekOrigin.Current);
        }

        return fml;
    }

    private static string ReadFString(BinaryReader reader)
    {
        // Assuming ReadFString reads a length-prefixed string
        var length = reader.ReadInt32();
        if (length == 0) return string.Empty;

        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
}

public class FileManifest
{
    public string Filename { get; set; }
    public string SymlinkTarget { get; set; }
    public byte[] Hash { get; set; }
    public byte Flags { get; set; }
    public List<string> InstallTags { get; set; }
    public List<ChunkPart> ChunkParts { get; set; }
    public long FileSize { get; set; }
    public byte[] HashMd5 { get; set; }
    public string MimeType { get; set; }
    public byte[] HashSha256 { get; set; }

    public FileManifest()
    {
        Filename = string.Empty;
        SymlinkTarget = string.Empty;
        Hash = new byte[0];
        Flags = 0;
        InstallTags = new List<string>();
        ChunkParts = new List<ChunkPart>();
        FileSize = 0;
        HashMd5 = new byte[0];
        MimeType = string.Empty;
        HashSha256 = new byte[0];
    }

    public bool ReadOnly => (Flags & 0x1) != 0;
    public bool Compressed => (Flags & 0x2) != 0;
    public bool Executable => (Flags & 0x4) != 0;

    public byte[] ShaHash => Hash;

    public override string ToString()
    {
        var chunkPartsStr = ChunkParts.Count <= 20
            ? string.Join(", ", ChunkParts.Select(cp => cp.ToString()))
            : string.Join(", ", ChunkParts.Take(20).Select(cp => cp.ToString()) + ", [...]");

        var installTagsStr = string.Join(", ", InstallTags);
        var hashStr = BitConverter.ToString(Hash).Replace("-", string.Empty);

        return $"<FileManifest (filename=\"{Filename}\", symlink_target=\"{SymlinkTarget}\", " +
               $"hash={hashStr}, flags={Flags}, install_tags=[{installTagsStr}], " +
               $"chunk_parts=[{chunkPartsStr}], file_size={FileSize})>";
    }
}

public class ChunkPart
{
    public int[] Guid { get; set; }
    public int Offset { get; set; }
    public int Size { get; set; }
    public long FileOffset { get; set; }

    private string _guidStr;
    private BigInteger _guidNum;

    public ChunkPart(int[] guid = null, int offset = 0, int size = 0, int fileOffset = 0)
    {
        Guid = guid ?? new int[4];
        Offset = offset;
        Size = size;
        FileOffset = fileOffset;
        _guidNum = -1;
    }

    public string GuidStr
    {
        get
        {
            if (_guidStr == null && Guid != null) _guidStr = string.Join("-", Guid.Select(g => g.ToString("x8")));

            return _guidStr;
        }
    }

    public BigInteger GuidNum
    {
        get
        {
            if (_guidNum == -1 && Guid != null)
                _guidNum = new BigInteger(Guid[3])
                           + (new BigInteger(Guid[2]) << 32)
                           + (new BigInteger(Guid[1]) << 64)
                           + (new BigInteger(Guid[0]) << 96);

            return _guidNum;
        }
    }

    public override string ToString()
    {
        var guidReadable = string.Join("-", Guid.Select(g => g.ToString("x8")));
        return $"<ChunkPart (guid={guidReadable}, offset={Offset}, size={Size}, file_offset={FileOffset})>";
    }
}
