using Crimson.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Providers.Ubisoft.Protocol;
using Serilog;

namespace Crimson.Providers.Ubisoft;

public interface IUbisoftCloudSaveSigner
{
    Task<IReadOnlyList<UbisoftSignedRequest>> SignAsync(UbisoftSession session, uint productId, UbisoftCloudSaveMethod method,
        IReadOnlyList<UbisoftCloudSaveItem> items, CancellationToken ct);
}

/// <summary>
/// Ubisoft keeps per-slot metadata in a profile entity (<c>upcCloudSaveUplayId_&lt;product&gt;</c>) and the bytes,
/// zlib-compressed, in S3 behind presigned URLs. The runtime marks a slot the game wrote with a zero-byte
/// <c>&lt;slot&gt;.save.upload</c> flag. Local data is never replaced without a backup.
/// </summary>
public sealed class UbisoftCloudSaves : ICloudSaveConflictSource
{
    private const string FlagSuffix = ".upload";
    private const string SaveExtension = ".save";
    private const int MaximumSaveBytes = 256 * 1024 * 1024;
    private static readonly string[] StorageHosts =
        ["ubisoft-uplay-savegames.s3.us-east-1.amazonaws.com", "ubisoft-uplay-savegames.s3.amazonaws.com"];

    private readonly string _root;
    private readonly UbisoftApi _api;
    private readonly HttpClient _storage;
    private readonly IUbisoftCloudSaveSigner _signer;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, CloudSaveSyncStatus> _status = new(StringComparer.Ordinal);

    public UbisoftCloudSaves(string providerRoot, UbisoftApi api, HttpClient storage, ILogger log)
        : this(providerRoot, api, storage, new DemuxSigner(), log) { }

    internal UbisoftCloudSaves(string providerRoot, UbisoftApi api, HttpClient storage, IUbisoftCloudSaveSigner signer,
        ILogger log, Func<DateTimeOffset>? clock = null)
    {
        _root = Path.GetFullPath(providerRoot);
        _api = api;
        _storage = storage;
        _signer = signer;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<string, CloudSaveSyncStatus>? SyncStatusChanged;

    public Func<CloudSaveConflict, Task<CloudSaveResolution>>? ConflictResolver { get; set; }

    public CloudSaveSyncStatus GetSyncStatus(string appName) =>
        _status.TryGetValue(appName, out var status) ? status : CloudSaveSyncStatus.Idle;

    /// <summary>Ends with a separator because games append the slot file name to it directly.</summary>
    public static string SaveDirectory(string providerRoot, string userId, uint productId)
    {
        // User IDs are opaque server values, never used unchecked as path components.
        var key = ProviderDataPaths.GameFileKey(new GameId(UbisoftLauncherProvider.ProviderId, userId));
        var directory = Path.Combine(Path.GetFullPath(providerRoot), "savegames", key,
            productId.ToString(CultureInfo.InvariantCulture));
        return Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
    }

    /// <summary>Waits until no sync runs for the product, so a new session cannot start mid-upload.</summary>
    public async Task WaitForIdleAsync(uint productId, CancellationToken ct = default)
    {
        var gate = Gate(productId);
        await gate.WaitAsync(ct);
        gate.Release();
    }

    /// <summary>Never throws.</summary>
    public Task SyncBeforeLaunchAsync(UbisoftSession session, UbisoftOwnedProduct product, CancellationToken ct = default) =>
        RunAsync(product.ProductId, "before launch", () => BeforeLaunchAsync(session, product, ct), ct);

    /// <summary>Never throws.</summary>
    public Task SyncAfterExitAsync(UbisoftSession session, UbisoftOwnedProduct product, CancellationToken ct = default) =>
        RunAsync(product.ProductId, "after exit", () => AfterExitAsync(session, product, ct), ct);

    private async Task RunAsync(uint productId, string stage, Func<Task> work, CancellationToken ct)
    {
        var appName = AppName(productId);
        var gate = Gate(productId);
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try { await work(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Sync problems never block playing; the flags stay so the next exit retries the upload.
            _log.Warning("Ubisoft cloud save sync {Stage} failed for {Product}: {ErrorType}", stage, productId, error.GetType().Name);
        }
        finally
        {
            SetStatus(appName, CloudSaveSyncStatus.Idle);
            gate.Release();
        }
    }

    private async Task BeforeLaunchAsync(UbisoftSession session, UbisoftOwnedProduct product, CancellationToken ct)
    {
        var productId = product.ProductId;
        var appName = AppName(productId);
        SetStatus(appName, CloudSaveSyncStatus.Checking);
        var directory = SaveDirectory(_root, session.UserId, productId);
        Directory.CreateDirectory(directory);
        var entity = await GetEntityAsync(session, productId, ct);
        if (entity is null) return;
        var file = await LoadStateAsync(productId, ct);
        var state = file.For(session.ProfileId);
        var (download, conflicts) = CompareBeforeLaunch(entity, state, directory);

        if (conflicts.Count > 0)
        {
            switch (await ResolveAsync(appName, conflicts))
            {
                case CloudSaveResolution.KeepCloud:
                    download.AddRange(conflicts.Select(conflict => conflict.Cloud));
                    break;
                case CloudSaveResolution.KeepLocal:
                    // The cloud copy is acknowledged; the local slot stays dirty and is uploaded after exit.
                    foreach (var (cloud, _) in conflicts)
                        state.Slots[cloud.Filename] = SlotState.Synced(cloud, state.Slots.GetValueOrDefault(cloud.Filename)?.LocalMd5);
                    break;
            }
        }

        if (download.Count > 0)
        {
            SetStatus(appName, CloudSaveSyncStatus.Downloading);
            await DownloadAsync(session, productId, directory, download, state, ct);
        }
        await SaveStateAsync(productId, file, ct);
    }

    private static (List<CloudSlot> Download, List<(CloudSlot Cloud, LocalSlot Local)> Conflicts) CompareBeforeLaunch(
        CloudEntity entity, ProfileState state, string directory)
    {
        var download = new List<CloudSlot>();
        var conflicts = new List<(CloudSlot Cloud, LocalSlot Local)>();
        foreach (var slot in entity.Slots)
        {
            var known = state.Slots.GetValueOrDefault(slot.Filename);
            var local = LocalSlot.Read(directory, slot.Filename);
            // A synced slot that is gone locally (deleted, or the save folder was reset) comes back from the cloud.
            if (known is not null && known.Matches(slot) && local.Exists) continue;
            if (local.Exists && string.Equals(local.Md5, slot.Hash, StringComparison.OrdinalIgnoreCase))
            {
                state.Slots[slot.Filename] = SlotState.Synced(slot, local.Md5);
                continue;
            }
            if (local.Exists && local.IsDirty(known)) conflicts.Add((slot, local));
            else download.Add(slot);
        }
        return (download, conflicts);
    }

    private async Task AfterExitAsync(UbisoftSession session, UbisoftOwnedProduct product, CancellationToken ct)
    {
        var productId = product.ProductId;
        var appName = AppName(productId);
        SetStatus(appName, CloudSaveSyncStatus.Checking);
        var directory = SaveDirectory(_root, session.UserId, productId);
        if (!Directory.Exists(directory)) return;
        var file = await LoadStateAsync(productId, ct);
        var state = file.For(session.ProfileId);
        var entity = await GetEntityAsync(session, productId, ct);
        var cloudSlots = (entity?.Slots ?? []).ToDictionary(slot => slot.Filename, StringComparer.OrdinalIgnoreCase);
        var (upload, conflicts) = CompareAfterExit(state, cloudSlots, directory);

        if (conflicts.Count > 0)
        {
            switch (await ResolveAsync(appName, conflicts))
            {
                case CloudSaveResolution.KeepLocal:
                    upload.AddRange(conflicts.Select(conflict => conflict.Local));
                    break;
                case CloudSaveResolution.KeepCloud:
                    SetStatus(appName, CloudSaveSyncStatus.Downloading);
                    await DownloadAsync(session, productId, directory, conflicts.Select(conflict => conflict.Cloud).ToList(), state, ct);
                    break;
            }
        }

        if (upload.Count > 0)
        {
            SetStatus(appName, CloudSaveSyncStatus.Uploading);
            await UploadAsync(session, product, entity, upload, state, ct);
        }
        await SaveStateAsync(productId, file, ct);
    }

    private static (List<LocalSlot> Upload, List<(CloudSlot Cloud, LocalSlot Local)> Conflicts) CompareAfterExit(
        ProfileState state, Dictionary<string, CloudSlot> cloudSlots, string directory)
    {
        var upload = new List<LocalSlot>();
        var conflicts = new List<(CloudSlot Cloud, LocalSlot Local)>();
        foreach (var local in LocalSlot.Enumerate(directory))
        {
            var known = state.Slots.GetValueOrDefault(local.Name);
            var cloud = cloudSlots.GetValueOrDefault(local.Name);
            var dirty = local.IsDirty(known) || (known is null && !SameBytes(cloud, local));
            if (!dirty) continue;
            if (SameBytes(cloud, local))
            {
                state.Slots[local.Name] = SlotState.Synced(cloud, local.Md5);
                local.ClearFlag();
                continue;
            }
            if (CloudChangedSince(cloud, known)) conflicts.Add((cloud, local));
            else upload.Add(local);
        }
        return (upload, conflicts);
    }

    private static bool SameBytes([NotNullWhen(true)] CloudSlot? cloud, LocalSlot local) =>
        cloud is not null && string.Equals(cloud.Hash, local.Md5, StringComparison.OrdinalIgnoreCase);

    private static bool CloudChangedSince([NotNullWhen(true)] CloudSlot? cloud, SlotState? known) =>
        cloud is not null && (known is null || !known.Matches(cloud));

    private async Task DownloadAsync(UbisoftSession session, uint productId, string directory, IReadOnlyList<CloudSlot> slots,
        ProfileState state, CancellationToken ct)
    {
        var urls = await SignAsync(session, productId, UbisoftCloudSaveMethod.Get,
            slots.Select(slot => new UbisoftCloudSaveItem(slot.Filename)).ToList(), ct);
        string? backup = null;
        foreach (var slot in slots)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, urls[slot.Filename]);
            using var response = await _storage.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Cloud save download failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            var compressed = await ReadBoundedAsync(response.Content, ct);
            var bytes = Inflate(compressed);
            VerifySlot(slot, bytes);
            var target = Path.Combine(directory, slot.Filename + SaveExtension);
            if (File.Exists(target))
            {
                backup ??= BackupDirectory(productId);
                Directory.CreateDirectory(backup);
                File.Copy(target, Path.Combine(backup, slot.Filename + SaveExtension), overwrite: true);
            }
            var temporary = target + ".crimson-tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, target, overwrite: true);
            TryDelete(target + FlagSuffix);
            state.Slots[slot.Filename] = SlotState.Synced(slot, Md5Hex(bytes));
            _log.Information("Ubisoft cloud save: restored slot {Slot} of {Product} ({Size} bytes)", slot.Filename, productId, bytes.Length);
        }
    }

    private async Task UploadAsync(UbisoftSession session, UbisoftOwnedProduct product, CloudEntity? entity,
        IReadOnlyList<LocalSlot> slots, ProfileState state, CancellationToken ct)
    {
        var productId = product.ProductId;
        var payloads = new List<UploadPayload>();
        foreach (var slot in slots)
        {
            var bytes = await File.ReadAllBytesAsync(slot.Path, ct);
            var compressed = Deflate(bytes);
            payloads.Add(new UploadPayload(slot, compressed, Convert.ToBase64String(MD5.HashData(compressed)), Md5Hex(bytes), bytes.Length));
        }
        var urls = await SignAsync(session, productId, UbisoftCloudSaveMethod.Put,
            payloads.Select(payload => new UbisoftCloudSaveItem(payload.Slot.Name, payload.Compressed.Length, payload.Md5Base64)).ToList(), ct);
        foreach (var payload in payloads) await PutAsync(urls[payload.Slot.Name], payload, ct);

        var now = _clock().ToUniversalTime();
        var (allSlots, written) = MergeSlots(entity, payloads, now);
        var body = EntityBody(entity, product, EntityObject(entity, allSlots, now));
        state.Revision = await SaveEntityAsync(session, entity, body, ct);

        foreach (var (payload, slot) in payloads.Zip(written))
        {
            payload.Slot.ClearFlag();
            state.Slots[slot.Filename] = SlotState.Synced(slot, payload.Hash);
        }
        _log.Information("Ubisoft cloud save: uploaded {Count} slot(s) of {Product}", payloads.Count, productId);
    }

    private async Task PutAsync(string url, UploadPayload payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(payload.Compressed) };
        request.Content.Headers.ContentMD5 = Convert.FromBase64String(payload.Md5Base64);
        request.Content.Headers.ContentLength = payload.Compressed.Length;
        using var response = await _storage.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloud save upload failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static (List<CloudSlot> All, List<CloudSlot> Written) MergeSlots(CloudEntity? entity,
        IReadOnlyList<UploadPayload> payloads, DateTimeOffset now)
    {
        var lastModified = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var slotsById = (entity?.Slots ?? []).ToList();
        var nextId = slotsById.Count == 0 ? 1 : slotsById.Max(slot => slot.Id) + 1;
        var written = new List<CloudSlot>();
        foreach (var payload in payloads)
        {
            var existing = slotsById.FindIndex(slot => string.Equals(slot.Filename, payload.Slot.Name, StringComparison.OrdinalIgnoreCase));
            var slot = new CloudSlot(existing >= 0 ? slotsById[existing].Id : nextId++, payload.Slot.Name, payload.Hash, payload.Size, lastModified);
            if (existing >= 0) slotsById[existing] = slot; else slotsById.Add(slot);
            written.Add(slot);
        }
        return (slotsById, written);
    }

    private static JsonObject EntityObject(CloudEntity? entity, IReadOnlyList<CloudSlot> slots, DateTimeOffset now)
    {
        var obj = entity?.Obj?.DeepClone().AsObject() ?? new JsonObject();
        obj["slots"] = new JsonArray(slots.Select(slot => (JsonNode)new JsonObject
        {
            ["id"] = slot.Id, ["filename"] = slot.Filename, ["hash"] = slot.Hash, ["size"] = slot.Size, ["lastModified"] = slot.LastModified
        }).ToArray());
        obj["timestamp"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        obj["pc_name"] = Environment.MachineName;
        return obj;
    }

    private static JsonObject EntityBody(CloudEntity? entity, UbisoftOwnedProduct product, JsonObject obj) => new()
    {
        ["name"] = EntityName(product.ProductId),
        ["type"] = entity?.Type ?? "cloudSave",
        ["spaceId"] = entity?.SpaceId ?? product.SpaceId,
        ["tags"] = entity?.Tags?.DeepClone() ?? new JsonArray(),
        ["obj"] = obj
    };

    private async Task<int> SaveEntityAsync(UbisoftSession session, CloudEntity? entity, JsonObject body, CancellationToken ct)
    {
        if (entity?.EntityId is { } entityId)
        {
            body["revision"] = entity.Revision + 1;
            await _api.Put(session, "/v1/profiles/entities/" + Uri.EscapeDataString(entityId), body, ct);
            return entity.Revision + 1;
        }
        // First upload for this product: the official client creates the entity the same way.
        await _api.Post(session, $"/v1/profiles/{Uri.EscapeDataString(session.ProfileId)}/entities", body, ct);
        return 1;
    }

    private async Task<CloudSaveResolution> ResolveAsync(string appName, IReadOnlyList<(CloudSlot Cloud, LocalSlot Local)> conflicts)
    {
        var resolver = ConflictResolver;
        if (resolver is null) return CloudSaveResolution.Skip;
        try
        {
            var conflict = new CloudSaveConflict(appName,
                conflicts.Max(pair => pair.Local.LastWriteUtc),
                conflicts.Max(pair => pair.Cloud.LastModifiedUtc),
                conflicts.Sum(pair => pair.Local.Size),
                conflicts.Sum(pair => pair.Cloud.Size));
            return await resolver(conflict);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Warning("Ubisoft cloud save conflict resolution failed for {AppName}: {ErrorType}", appName, error.GetType().Name);
            return CloudSaveResolution.Skip;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> SignAsync(UbisoftSession session, uint productId,
        UbisoftCloudSaveMethod method, IReadOnlyList<UbisoftCloudSaveItem> items, CancellationToken ct)
    {
        var signed = await _signer.SignAsync(session, productId, method, items, ct);
        var wanted = items.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var request in signed)
        {
            var name = SignedSlotName(request.Url, session.ProfileId, productId);
            if (!wanted.Contains(name) || !result.TryAdd(name, request.Url))
                throw new InvalidDataException("Unexpected cloud save destination.");
        }
        if (result.Count != wanted.Count) throw new InvalidDataException("Incomplete cloud save URL response.");
        return result;
    }

    /// <summary>The slot a signed URL addresses: <c>{profileId}/{productId}/{slot}.savegame</c> on the save bucket.</summary>
    private static string SignedSlotName(string url, string profileId, uint productId)
    {
        if (!IsStorageUri(url, out var uri)) throw new InvalidDataException("Unexpected cloud save destination.");
        var segments = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Split('/');
        if (segments.Length != 3 || !string.Equals(segments[0], profileId, StringComparison.OrdinalIgnoreCase) ||
            segments[1] != productId.ToString(CultureInfo.InvariantCulture) || !segments[2].EndsWith(".savegame", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected cloud save destination.");
        return segments[2][..^".savegame".Length];
    }

    private static bool IsStorageUri(string url, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
        StorageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private async Task<CloudEntity?> GetEntityAsync(UbisoftSession session, uint productId, CancellationToken ct)
    {
        JsonElement json;
        try
        {
            json = await _api.Get(session, $"/v1/profiles/{Uri.EscapeDataString(session.ProfileId)}/entities?name="
                + Uri.EscapeDataString(EntityName(productId)), ct);
        }
        catch (UbisoftApiException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
        return CloudEntity.Parse(json);
    }

    private static string EntityName(uint productId) => "upcCloudSaveUplayId_" + productId.ToString(CultureInfo.InvariantCulture);

    private static string AppName(uint productId) =>
        new GameId(UbisoftLauncherProvider.ProviderId, productId.ToString(CultureInfo.InvariantCulture)).ToString();

    private string BackupDirectory(uint productId) => Path.Combine(_root, "savegames-backup",
        productId.ToString(CultureInfo.InvariantCulture), _clock().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture));

    private string StatePath(uint productId) =>
        Path.Combine(_root, "cloudsaves", productId.ToString(CultureInfo.InvariantCulture) + ".json");

    private async Task<StateFile> LoadStateAsync(uint productId, CancellationToken ct)
    {
        try
        {
            var path = StatePath(productId);
            if (!File.Exists(path)) return new StateFile();
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, UbisoftCamelJsonContext.Default.StateFile, ct) ?? new StateFile();
        }
        catch (JsonException) { return new StateFile(); }
    }

    private async Task SaveStateAsync(uint productId, StateFile file, CancellationToken ct)
    {
        var path = StatePath(productId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var stream = File.Create(path + ".tmp"))
            await JsonSerializer.SerializeAsync(stream, file, UbisoftCamelJsonContext.Default.StateFile, ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private SemaphoreSlim Gate(uint productId) => _gates.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));

    private void SetStatus(string appName, CloudSaveSyncStatus status)
    {
        var previous = _status.GetValueOrDefault(appName, CloudSaveSyncStatus.Idle);
        if (status == CloudSaveSyncStatus.Idle) _status.TryRemove(appName, out _);
        else _status[appName] = status;
        if (previous != status) SyncStatusChanged?.Invoke(appName, status);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumSaveBytes) throw new InvalidDataException("Cloud save is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumSaveBytes) throw new InvalidDataException("Cloud save is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return output.ToArray();
    }

    internal static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = zlib.Read(chunk)) > 0)
        {
            if (output.Length + read > MaximumSaveBytes) throw new InvalidDataException("Cloud save is too large.");
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    private static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    /// <summary>The entity's hash and size describe the uncompressed bytes; never replace a local slot without them.</summary>
    private static void VerifySlot(CloudSlot slot, byte[] bytes)
    {
        if (slot.Size > 0 && bytes.LongLength != slot.Size)
            throw new InvalidDataException("The downloaded cloud save has the wrong size.");
        if (slot.Hash.Length == 32 && !string.Equals(Md5Hex(bytes), slot.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded cloud save failed its MD5 check.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Slot names become file names, so only plain names are accepted.</summary>
    private static bool IsSafeName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name != "." && name != ".." &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0 &&
        name.Trim() == name && !name.EndsWith('.');

    internal sealed record CloudSlot(int Id, string Filename, string Hash, long Size, string LastModified)
    {
        public DateTimeOffset LastModifiedUtc => DateTimeOffset.TryParse(LastModified, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : DateTimeOffset.MinValue;
    }

    private sealed record CloudEntity(string? EntityId, int Revision, string? Type, string? SpaceId, JsonNode? Tags,
        JsonObject? Obj, IReadOnlyList<CloudSlot> Slots)
    {
        public static CloudEntity? Parse(JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("entities", out var list)) json = list;
            if (json.ValueKind == JsonValueKind.Array) json = json.EnumerateArray().FirstOrDefault();
            if (json.ValueKind != JsonValueKind.Object) return null;
            var node = JsonNode.Parse(json.GetRawText())!.AsObject();
            var obj = node["obj"] as JsonObject;
            return new CloudEntity(Text(node["entityId"]), Integer(node["revision"]), Text(node["type"]), Text(node["spaceId"]),
                node["tags"], obj, ReadSlots(obj));
        }

        private static List<CloudSlot> ReadSlots(JsonObject? obj)
        {
            var slots = new List<CloudSlot>();
            if (obj?["slots"] is not JsonArray array) return slots;
            foreach (var item in array.OfType<JsonObject>())
            {
                var filename = Text(item["filename"]);
                if (!IsSafeName(filename)) continue;
                if (slots.Any(slot => string.Equals(slot.Filename, filename, StringComparison.OrdinalIgnoreCase))) continue;
                slots.Add(new CloudSlot(Integer(item["id"]), filename, Text(item["hash"]) ?? "",
                    Integer(item["size"]), Text(item["lastModified"]) ?? ""));
            }
            return slots;
        }

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : node is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.ToJsonString() : null;

        private static int Integer(JsonNode? node) =>
            int.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private sealed class LocalSlot
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public bool Exists { get; init; }
        public string Md5 { get; init; } = "";
        public long Size { get; init; }
        public DateTimeOffset LastWriteUtc { get; init; }
        public bool Flagged { get; init; }

        public bool IsDirty(SlotState? known) =>
            Exists && (Flagged || (known is not null && !string.Equals(Md5, known.LocalMd5, StringComparison.OrdinalIgnoreCase)));

        public void ClearFlag() => TryDelete(Path + FlagSuffix);

        public static LocalSlot Read(string directory, string name)
        {
            var path = System.IO.Path.Combine(directory, name + SaveExtension);
            var info = new FileInfo(path);
            if (!info.Exists) return new LocalSlot { Name = name, Path = path };
            if (info.Length > MaximumSaveBytes) throw new InvalidDataException("Cloud save is too large.");
            using var stream = info.OpenRead();
            return new LocalSlot
            {
                Name = name, Path = path, Exists = true, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                Md5 = Convert.ToHexStringLower(MD5.HashData(stream)), Flagged = File.Exists(path + FlagSuffix)
            };
        }

        public static IEnumerable<LocalSlot> Enumerate(string directory) => Directory
            .EnumerateFiles(directory, "*" + SaveExtension)
            .Where(path => path.EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase))
            .Select(path => System.IO.Path.GetFileNameWithoutExtension(path))
            .Where(IsSafeName)
            .Select(name => Read(directory, name))
            .ToList();
    }

    internal sealed class StateFile
    {
        public Dictionary<string, ProfileState> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ProfileState For(string profileId)
        {
            if (!Profiles.TryGetValue(profileId, out var state)) Profiles[profileId] = state = new ProfileState();
            state.Slots = new Dictionary<string, SlotState>(state.Slots ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
    }

    internal sealed class ProfileState
    {
        public int Revision { get; set; }
        public Dictionary<string, SlotState> Slots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record SlotState(string CloudHash, string CloudLastModified, long CloudSize, string? LocalMd5)
    {
        public bool Matches(CloudSlot slot) =>
            string.Equals(CloudHash, slot.Hash, StringComparison.OrdinalIgnoreCase) && CloudLastModified == slot.LastModified;

        public static SlotState Synced(CloudSlot slot, string? localMd5) => new(slot.Hash, slot.LastModified, slot.Size, localMd5);
    }

    private sealed record UploadPayload(LocalSlot Slot, byte[] Compressed, string Md5Base64, string Hash, int Size);

    private sealed class DemuxSigner : IUbisoftCloudSaveSigner
    {
        public async Task<IReadOnlyList<UbisoftSignedRequest>> SignAsync(UbisoftSession session, uint productId,
            UbisoftCloudSaveMethod method, IReadOnlyList<UbisoftCloudSaveItem> items, CancellationToken ct)
        {
            await using var connection = await UbisoftDemuxConnection.ConnectAsync(session.Ticket, ct);
            var ownership = await UbisoftOwnershipService.OpenAsync(connection, ct);
            var token = await ownership.GetOwnershipTokenAsync(productId, ct);
            var service = await UbisoftCloudSaveService.OpenAsync(connection, ct);
            return await service.GetUrlsAsync(token, productId, method, items, ct);
        }
    }
}
