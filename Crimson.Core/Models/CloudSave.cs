using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Crimson.Models;

/// <summary>
/// Result of comparing the local save directory against the newest cloud save manifest,
/// mirroring legendary's SaveGameStatus.
/// </summary>
public enum CloudSaveState
{
    LocalNewer,
    RemoteNewer,
    SameAge,
    NoSave
}

/// <summary>
/// Active cloud sync work for a game. Surfaced on the Play button on the game info page.
/// </summary>
public enum CloudSaveSyncStatus
{
    Idle,
    Checking,
    Downloading,
    Uploading
}

/// <summary>
/// User choice when the local and cloud saves diverge.
/// </summary>
public enum CloudSaveResolution
{
    KeepLocal,
    KeepCloud,
    Skip
}

/// <summary>
/// A cloud save manifest entry as listed by the savesync API. The manifest name encodes the
/// save timestamp (yyyy.MM.dd-HH.mm.ss.manifest, UTC), which is the authoritative remote time.
/// </summary>
public sealed record RemoteSaveInfo(
    string AppName,
    string Path,
    string ManifestName,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Details shown to the user when local and cloud saves have diverged and a direction
/// cannot be chosen automatically.
/// </summary>
public sealed record CloudSaveConflict(
    string AppName,
    DateTimeOffset LocalTimestampUtc,
    DateTimeOffset RemoteTimestampUtc,
    long LocalSizeBytes,
    long RemoteSizeBytes);

/// <summary>
/// Snapshot of the local save directory: latest write time and total size of the files
/// covered by the cloud include/exclude filters.
/// </summary>
public readonly record struct LocalSaveInfo(DateTimeOffset? TimestampUtc, long SizeBytes, int FileCount)
{
    public bool HasFiles => FileCount > 0;
}

/// <summary>
/// One entry of the savesync API file listing. Read/write links are pre-signed URLs that
/// need no further authentication.
/// </summary>
public sealed class CloudSaveFileEntry
{
    [JsonPropertyName("readLink")]
    public string? ReadLink { get; set; }

    [JsonPropertyName("writeLink")]
    public string? WriteLink { get; set; }

    [JsonPropertyName("lastModified")]
    public string? LastModified { get; set; }
}

public sealed class CloudSaveListing
{
    [JsonPropertyName("files")]
    public Dictionary<string, CloudSaveFileEntry>? Files { get; set; }
}
