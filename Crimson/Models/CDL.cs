using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Crimson.Models;

public class CDL
{
    public int Version { get; set; }
    public int Size { get; set; }
    public int Count { get; set; }
    public List<ChunkInfo> Elements { get; set; }
    private int _manifestVersion;
    private Dictionary<string, int> _guidMap;
    private Dictionary<BigInteger, int> _guidIntMap;
    private Dictionary<string, int> _pathMap;

    public CDL(int manifestVersion = 18)
    {
        Version = 0;
        Size = 0;
        Count = 0;
        Elements = new List<ChunkInfo>();
        _manifestVersion = manifestVersion;
    }

    public ChunkInfo GetChunkByPath(string path)
    {
        if (_pathMap == null)
        {
            _pathMap = new Dictionary<string, int>();
            for (var i = 0; i < Elements.Count; i++) _pathMap[Elements[i].Path] = i;
        }

        if (_pathMap.TryGetValue(path, out var index))
            return Elements[index];
        else
            throw new ArgumentException($"Invalid path! \"{path}\"");
    }

    public ChunkInfo GetChunkByGuid(object guid)
    {
        if (guid is int)
            return GetChunkByGuidNum((BigInteger)guid);
        else if (guid is string)
            return GetChunkByGuidStr((string)guid);
        else
            throw new ArgumentException("Invalid GUID type!");
    }

    public ChunkInfo GetChunkByGuidStr(string guid)
    {
        if (_guidMap == null)
        {
            _guidMap = new Dictionary<string, int>();
            for (var i = 0; i < Elements.Count; i++) _guidMap[Elements[i].GuidStr.ToLower()] = i;
        }

        if (_guidMap.TryGetValue(guid.ToLower(), out var index))
            return Elements[index];
        else
            throw new ArgumentException($"Invalid GUID! {guid}");
    }

    public ChunkInfo GetChunkByGuidNum(BigInteger guidInt)
    {
        if (_guidIntMap == null)
        {
            _guidIntMap = new Dictionary<BigInteger, int>();
            for (var i = 0; i < Elements.Count; i++) _guidIntMap[Elements[i].GuidNum] = i;
        }

        if (_guidIntMap.TryGetValue(guidInt, out var index))
            return Elements[index];
        else
            throw new ArgumentException($"Invalid GUID! {guidInt.ToString("x")}");
    }

    public static CDL Read(Stream bio, int manifestVersion = 18)
    {
        var cdl = new CDL(manifestVersion);
        var reader = new BinaryReader(bio);

        var cdlStart = bio.Position;
        cdl.Size = reader.ReadInt32();
        cdl.Version = reader.ReadByte();
        cdl.Count = reader.ReadInt32();

        // Read elements
        for (var i = 0; i < cdl.Count; i++) cdl.Elements.Add(new ChunkInfo(manifestVersion));

        foreach (var chunk in cdl.Elements)
        {
            chunk.Guid = new int[4];
            for (var i = 0; i < 4; i++) chunk.Guid[i] = reader.ReadInt32();
        }

        // Read Hashes
        foreach (var chunk in cdl.Elements) chunk.Hash = reader.ReadInt64();

        // Read SHA1 Hashes
        foreach (var chunk in cdl.Elements) chunk.ShaHash = reader.ReadBytes(20);

        // Read Group Numbers
        foreach (var chunk in cdl.Elements) chunk.GroupNum = reader.ReadByte();

        // Read Window Sizes
        foreach (var chunk in cdl.Elements) chunk.WindowSize = reader.ReadInt32();

        // Read File Sizes
        foreach (var chunk in cdl.Elements) chunk.FileSize = reader.ReadInt64();

        var sizeRead = bio.Position - cdlStart;
        if (sizeRead == cdl.Size) return cdl;
        // Log warning here and seek forward
        cdl.Version = 0;
        bio.Seek(cdl.Size - sizeRead, SeekOrigin.Current);

        return cdl;
    }
}

public class ChunkInfo
{
    public int[] Guid { get; set; }
    public long Hash { get; set; }
    public byte[] ShaHash { get; set; }
    public int WindowSize { get; set; }
    public long FileSize { get; set; }

    private readonly int _manifestVersion;
    private int? _groupNum;
    private string _guidStr;
    private BigInteger _guidNum;

    public ChunkInfo(int manifestVersion = 18)
    {
        Hash = 0;
        ShaHash = new byte[0];
        WindowSize = 0;
        FileSize = 0;
        _manifestVersion = manifestVersion;
        _guidNum = -1;
    }

    public override string ToString()
    {
        return
            $"<ChunkInfo (guid={GuidStr}, hash={Hash}, sha_hash={BitConverter.ToString(ShaHash).Replace("-", "")}, group_num={GroupNum}, window_size={WindowSize}, file_size={FileSize})>";
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

    public int GroupNum
    {
        get
        {
            if (!_groupNum.HasValue && Guid != null)
            {
                var guidBytes = Guid.SelectMany(BitConverter.GetBytes).ToArray();
                var crc = Crc32.Compute(guidBytes);
                _groupNum = (int)(crc % 100);
            }

            return _groupNum ?? throw new InvalidOperationException("GUID is not set.");
        }
        set => _groupNum = value;
    }

    public string Path
    {
        get
        {
            var guidHex = string.Join("", Guid.Select(g => g.ToString("X8")));
            return $"{GetChunkDir(_manifestVersion)}/{GroupNum:D2}/{Hash:X16}_{guidHex}.chunk";
        }
    }

    private static class Crc32
    {
        public static uint Compute(byte[] bytes)
        {
            var crc = 0xFFFFFFFF;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }

            return ~crc;
        }
    }

    private static string GetChunkDir(int manifestVersion)
    {
        return manifestVersion switch
        {
            >= 15 => "ChunksV4",
            >= 6 => "ChunksV3",
            >= 3 => "ChunksV2",
            _ => "Chunks"
        };
    }
}