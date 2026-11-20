using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text;
using System;

namespace Crimson.Models;

internal static class JsonManifestReader
{
    public static bool IsJson(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (!char.IsWhiteSpace((char)value))
                return value == (byte)'{';
        }

        return false;
    }

    public static Manifest Read(byte[] data)
    {
        try
        {
            return ReadCore(data);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            throw new InvalidDataException("JSON manifest is malformed.", exception);
        }
    }

    private static Manifest ReadCore(byte[] data)
    {
        if (data.Length > EpicProtocolLimits.MaximumManifestBytes)
            throw new InvalidDataException("JSON manifest exceeds the supported size limit.");

        using var document = JsonDocument.Parse(data, new JsonDocumentOptions
        {
            MaxDepth = 64,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        // Every field here is optional in legendary (json_data.pop with a default). JSON
        // manifests are served for the oldest titles, which routinely omit launch/prereq keys.
        var manifestVersion = DecodeInt32(GetOptionalString(root, "ManifestFileVersion") ?? "013000000000");
        if (manifestVersion is < 1 or > 24)
            throw new InvalidDataException($"JSON manifest version {manifestVersion} is unsupported.");

        var meta = ReadManifestMeta(root, manifestVersion);
        var cdl = ReadChunkDataList(root, manifestVersion);
        var files = ReadFileManifestList(root, cdl);
        return new Manifest
        {
            ManifestMeta = meta,
            CDL = cdl,
            FileManifestList = files,
            CustomFields = ReadCustomFields(root)
        };
    }

    private static ManifestMeta ReadManifestMeta(JsonElement root, int manifestVersion)
    {
        var meta = new ManifestMeta
        {
            FeatureLevel = checked((uint)manifestVersion),
            IsFileData = GetOptionalBoolean(root, "bIsFileData"),
            AppId = unchecked((uint)DecodeInt32(GetOptionalString(root, "AppID") ?? "000000000000")),
            AppName = GetStringOrEmpty(root, "AppNameString"),
            BuildVersion = GetStringOrEmpty(root, "BuildVersionString"),
            LaunchExe = GetStringOrEmpty(root, "LaunchExeString"),
            LaunchCommand = GetStringOrEmpty(root, "LaunchCommand"),
            PrereqName = GetStringOrEmpty(root, "PrereqName"),
            PrereqPath = GetStringOrEmpty(root, "PrereqPath"),
            PrereqArgs = GetStringOrEmpty(root, "PrereqArgs")
        };
        if (root.TryGetProperty("PrereqIds", out var prereqIds))
        {
            if (prereqIds.ValueKind != JsonValueKind.Array ||
                prereqIds.GetArrayLength() > 4_096)
                throw new InvalidDataException("JSON manifest prerequisites are invalid.");
            meta.PrereqIds = prereqIds.EnumerateArray()
                .Select(element => GetBoundedString(element, "PrereqIds"))
                .ToList();
        }

        return meta;
    }

    private static CDL ReadChunkDataList(JsonElement root, int manifestVersion)
    {
        var chunkHashes = GetRequiredObject(root, "ChunkHashList");
        var chunkSha = GetRequiredObject(root, "ChunkShaList");
        var chunkGroups = GetRequiredObject(root, "DataGroupList");
        var chunkFileSizes = GetRequiredObject(root, "ChunkFilesizeList");
        if (chunkHashes.EnumerateObject().Count() > EpicProtocolLimits.MaximumChunkCount)
            throw new InvalidDataException("JSON manifest chunk count exceeds the supported limit.");

        var cdl = new CDL(manifestVersion) { Version = 0 };
        foreach (var hashProperty in chunkHashes.EnumerateObject())
            cdl.Elements.Add(ReadChunkInfo(hashProperty, chunkSha, chunkGroups, chunkFileSizes, manifestVersion));

        cdl.Count = cdl.Elements.Count;
        return cdl;
    }

    private static ChunkInfo ReadChunkInfo(
        JsonProperty hashProperty,
        JsonElement chunkSha,
        JsonElement chunkGroups,
        JsonElement chunkFileSizes,
        int manifestVersion)
    {
        var guid = ParseGuid(hashProperty.Name);
        var guidText = hashProperty.Name;
        if (!chunkSha.TryGetProperty(guidText, out var shaElement) ||
            !chunkGroups.TryGetProperty(guidText, out var groupElement) ||
            !chunkFileSizes.TryGetProperty(guidText, out var sizeElement))
            throw new InvalidDataException($"JSON manifest chunk metadata is incomplete: {guidText}.");

        var sha = ParseHexBytes(GetBoundedString(shaElement, "ChunkShaList"), 20);
        var group = DecodeByteString(GetBoundedString(groupElement, "DataGroupList"));
        var fileSize = DecodeInt64(GetBoundedString(sizeElement, "ChunkFilesizeList"));
        if (group > 99 || fileSize < 0 || fileSize > EpicProtocolLimits.MaximumChunkBytes + 4_096L)
            throw new InvalidDataException($"JSON manifest chunk metadata is out of range: {guidText}.");

        return new ChunkInfo(manifestVersion)
        {
            Guid = guid,
            Hash = DecodeInt64(GetBoundedString(hashProperty.Value, "ChunkHashList")),
            ShaHash = sha,
            GroupNum = group,
            WindowSize = 1024 * 1024,
            FileSize = fileSize
        };
    }

    private static FileManifestList ReadFileManifestList(JsonElement root, CDL cdl)
    {
        var filesElement = root.GetProperty("FileManifestList");
        if (filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() > EpicProtocolLimits.MaximumFileCount)
            throw new InvalidDataException("JSON file manifest list is invalid.");

        var files = new FileManifestList { Version = 0 };
        long cumulativeParts = 0;
        foreach (var fileElement in filesElement.EnumerateArray())
            files.Elements.Add(ReadFileManifest(fileElement, cdl, ref cumulativeParts));

        files.Count = files.Elements.Count;
        return files;
    }

    private static FileManifest ReadFileManifest(JsonElement fileElement, CDL cdl, ref long cumulativeParts)
    {
        // No case-insensitive uniqueness check, matching the binary reader.
        var file = new FileManifest
        {
            Filename = GetOptionalString(fileElement, "Filename") ?? string.Empty,
            Hash = DecodeDecimalBytes(GetRequiredString(fileElement, "FileHash"), 20),
            Flags = ReadFileFlags(fileElement)
        };
        ReadInstallTags(fileElement, file);
        ReadChunkParts(fileElement, file.Filename, cdl, file, ref cumulativeParts);
        return file;
    }

    private static byte ReadFileFlags(JsonElement fileElement)
    {
        byte flags = 0;
        if (GetOptionalBoolean(fileElement, "bIsReadOnly")) flags |= 0x1;
        if (GetOptionalBoolean(fileElement, "bIsCompressed")) flags |= 0x2;
        if (GetOptionalBoolean(fileElement, "bIsUnixExecutable")) flags |= 0x4;
        return flags;
    }

    private static void ReadInstallTags(JsonElement fileElement, FileManifest file)
    {
        if (!fileElement.TryGetProperty("InstallTags", out var installTags) ||
            installTags.ValueKind != JsonValueKind.Array)
            return;

        foreach (var tag in installTags.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String)
                file.InstallTags.Add(tag.GetString() ?? string.Empty);
        }
    }

    private static void ReadChunkParts(
        JsonElement fileElement,
        string filename,
        CDL cdl,
        FileManifest file,
        ref long cumulativeParts)
    {
        if (!fileElement.TryGetProperty("FileChunkParts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() > EpicProtocolLimits.MaximumChunkPartsPerFile)
            throw new InvalidDataException($"JSON chunk parts are invalid for {filename}.");

        long fileOffset = 0;
        foreach (var partElement in parts.EnumerateArray())
        {
            cumulativeParts++;
            if (cumulativeParts > EpicProtocolLimits.MaximumCumulativeChunkParts)
                throw new InvalidDataException("JSON cumulative chunk part count exceeds the supported limit.");
            var offset = DecodeInt32(GetRequiredString(partElement, "Offset"));
            var size = DecodeInt32(GetRequiredString(partElement, "Size"));
            if (offset < 0 || size < 0 || (long)offset + size > EpicProtocolLimits.MaximumChunkBytes)
                throw new InvalidDataException($"JSON chunk part range is invalid for {filename}.");

            var part = new ChunkPart(ParseGuid(GetRequiredString(partElement, "Guid")), offset, size)
            {
                FileOffset = fileOffset
            };
            _ = cdl.GetChunkByGuidNum(part.GuidNum);
            file.ChunkParts.Add(part);
            fileOffset = checked(fileOffset + size);
        }

        file.FileSize = fileOffset;
    }

    private static CustomFields ReadCustomFields(JsonElement root)
    {
        var customFields = new CustomFields { Version = 0 };
        if (!root.TryGetProperty("CustomFields", out var customElement))
            return customFields;

        if (customElement.ValueKind != JsonValueKind.Object ||
            customElement.EnumerateObject().Count() > EpicProtocolLimits.MaximumCustomFields)
            throw new InvalidDataException("JSON custom fields are invalid.");
        foreach (var property in customElement.EnumerateObject())
            customFields[property.Name] = GetBoundedString(property.Value, "CustomFields");
        customFields.Count = customElement.EnumerateObject().Count();
        return customFields;
    }

    private static JsonElement GetRequiredObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"JSON manifest field {name} is missing or invalid.");
        return value;
    }

    private static string? GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetStringOrEmpty(JsonElement root, string name) =>
        GetOptionalString(root, name) ?? string.Empty;

    private static bool GetOptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new InvalidDataException($"JSON manifest field {name} is missing.");
        return GetBoundedString(value, name);
    }

    private static bool GetRequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"JSON manifest field {name} is missing or invalid.");
        return value.GetBoolean();
    }

    private static string GetBoundedString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"JSON manifest field {name} is not a string.");
        var text = value.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > EpicProtocolLimits.MaximumStringBytes)
            throw new InvalidDataException($"JSON manifest field {name} exceeds the supported length.");
        return text;
    }

    private static int[] ParseGuid(string value)
    {
        if (value.Length != 32)
            throw new InvalidDataException("JSON chunk GUID length is invalid.");
        try
        {
            return Enumerable.Range(0, 4)
                .Select(index => unchecked((int)uint.Parse(
                    value.AsSpan(index * 8, 8),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture)))
                .ToArray();
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("JSON chunk GUID is invalid.", exception);
        }
    }

    private static byte[] ParseHexBytes(string value, int expectedLength)
    {
        try
        {
            var bytes = Convert.FromHexString(value);
            return bytes.Length == expectedLength
                ? bytes
                : throw new InvalidDataException("JSON hash length is invalid.");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("JSON hash is invalid.", exception);
        }
    }

    private static byte[] DecodeDecimalBytes(string value, int expectedLength)
    {
        if (value.Length != expectedLength * 3)
            throw new InvalidDataException("JSON byte string length is invalid.");
        var bytes = new byte[expectedLength];
        for (var index = 0; index < expectedLength; index++)
        {
            if (!byte.TryParse(value.AsSpan(index * 3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
                throw new InvalidDataException("JSON byte string is invalid.");
        }
        return bytes;
    }

    private static byte DecodeByteString(string value) => DecodeDecimalBytes(value, 1)[0];

    private static int DecodeInt32(string value)
    {
        var bytes = DecodeDecimalBytes(value, 4);
        return BitConverter.ToInt32(bytes);
    }

    private static long DecodeInt64(string value)
    {
        var bytes = DecodeDecimalBytes(value, 8);
        return BitConverter.ToInt64(bytes);
    }
}
