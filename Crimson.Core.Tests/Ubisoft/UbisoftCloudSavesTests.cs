using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Crimson.Models;
using Crimson.Providers.Ubisoft;
using Crimson.Providers.Ubisoft.Protocol;
using Serilog;
using static Crimson.Core.Tests.Ubisoft.UbisoftAuthenticationTests;
using static Crimson.Core.Tests.Ubisoft.UbisoftLibraryTests;

namespace Crimson.Core.Tests.Ubisoft;

public sealed class UbisoftCloudSavesTests : IDisposable
{
    private const uint ProductId = 2688;
    private static readonly byte[] CloudV1 = Encoding.UTF8.GetBytes("cloud save, first revision");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crimson-ubisoft-saves-" + Guid.NewGuid().ToString("N"));
    private readonly UbisoftSession _session = new() { Ticket = "ticket", SessionId = "session", ProfileId = "profile", UserId = "user" };
    private readonly UbisoftOwnedProduct _product = Product(ProductId, config: GameConfig);
    private readonly List<(string Method, string Path, string Body)> _api = [];
    private readonly List<(HttpMethod Method, byte[] Body, byte[]? ContentMd5)> _storage = [];
    private readonly Signer _signer = new();
    private JsonObject _entity = Entity(4, Slot(Md5Hex(CloudV1), "2026-11-01T10:00:00Z", CloudV1.Length));
    private byte[] _cloudBytes = CloudV1;

    public UbisoftCloudSavesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Provider => Path.Combine(_root, "provider");
    private string Saves => UbisoftCloudSaves.SaveDirectory(Provider, _session.UserId, ProductId);
    private string SlotFile => Path.Combine(Saves, "1.save");

    private static string Md5Hex(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    private static JsonObject Slot(string hash, string lastModified, int size) => new()
    {
        ["id"] = 1, ["filename"] = "1", ["hash"] = hash, ["size"] = size, ["lastModified"] = lastModified
    };

    private static JsonObject Entity(int revision, JsonObject slot) => new()
    {
        ["entityId"] = "entity-1", ["profileId"] = "profile", ["spaceId"] = "space", ["type"] = "cloudSave",
        ["name"] = "upcCloudSaveUplayId_2688", ["tags"] = new JsonArray(), ["revision"] = revision,
        ["obj"] = new JsonObject { ["slots"] = new JsonArray(slot), ["timestamp"] = "1792522701", ["pc_name"] = "OTHER-PC" }
    };

    private UbisoftCloudSaves Create()
    {
        var api = new UbisoftApi(new HttpClient(new Handler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            _api.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return request.Method == HttpMethod.Get
                ? Json(new JsonObject { ["entities"] = new JsonArray(_entity.DeepClone()) })
                : Json(new { });
        })));
        var storage = new HttpClient(new Handler(async request =>
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync();
            _storage.Add((request.Method, body, request.Content?.Headers.ContentMD5));
            if (request.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zlib(_cloudBytes)) };
        }));
        return new UbisoftCloudSaves(Provider, api, storage, _signer, new LoggerConfiguration().CreateLogger(),
            () => new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    }

    private static byte[] Zlib(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Unzlib(byte[] bytes)
    {
        using var zlib = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private async Task<UbisoftCloudSaves> InSync()
    {
        Directory.CreateDirectory(Saves);
        await File.WriteAllBytesAsync(SlotFile, CloudV1);
        var saves = Create();
        await saves.SyncBeforeLaunchAsync(_session, _product);
        Assert.Empty(_storage);
        return saves;
    }

    [Fact]
    public async Task A_cloud_slot_with_the_wrong_hash_does_not_replace_the_local_save()
    {
        Directory.CreateDirectory(Saves);
        await File.WriteAllTextAsync(SlotFile, "older local save");
        _cloudBytes = Encoding.UTF8.GetBytes("corrupt or wrong cloud bytes");

        await Create().SyncBeforeLaunchAsync(_session, _product);

        Assert.Equal("older local save", await File.ReadAllTextAsync(SlotFile));
        Assert.False(Directory.Exists(Path.Combine(Provider, "savegames-backup")));
    }

    [Fact]
    public async Task ANewerCloudSlotReplacesTheLocalOneAfterABackup()
    {
        Directory.CreateDirectory(Saves);
        await File.WriteAllTextAsync(SlotFile, "older local save");

        await Create().SyncBeforeLaunchAsync(_session, _product);

        Assert.Equal(CloudV1, await File.ReadAllBytesAsync(SlotFile));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(Provider, "savegames-backup", "2688"), "1.save", SearchOption.AllDirectories));
        Assert.Equal("older local save", await File.ReadAllTextAsync(backup));
        Assert.Equal(UbisoftCloudSaveMethod.Get, _signer.Methods.Single());
        Assert.True(File.Exists(Path.Combine(Provider, "cloudsaves", "2688.json")));
        Assert.DoesNotContain(_api, call => call.Method != "GET");
    }

    [Fact]
    public async Task ADirtySlotIsUploadedAndTheEntityRevisionBumped()
    {
        var saves = await InSync();
        var written = Encoding.UTF8.GetBytes("the game wrote a new save");
        await File.WriteAllBytesAsync(SlotFile, written);
        await File.WriteAllBytesAsync(SlotFile + ".upload", []);

        await saves.SyncAfterExitAsync(_session, _product);

        var put = Assert.Single(_storage, call => call.Method == HttpMethod.Put);
        Assert.Equal(written, Unzlib(put.Body));
        Assert.Equal(MD5.HashData(put.Body), put.ContentMd5);
        var item = Assert.Single(_signer.Items);
        Assert.Equal(put.Body.Length, item.WriteLength);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(put.Body)), item.Md5Base64);

        var update = Assert.Single(_api, call => call.Method == "PUT");
        Assert.Equal("/v1/profiles/entities/entity-1", update.Path);
        var body = JsonNode.Parse(update.Body)!;
        Assert.Equal(5, body["revision"]!.GetValue<int>());
        Assert.Equal("upcCloudSaveUplayId_2688", body["name"]!.GetValue<string>());
        var slot = body["obj"]!["slots"]!.AsArray().Single()!;
        Assert.Equal(Md5Hex(written), slot["hash"]!.GetValue<string>());
        Assert.Equal(written.Length, slot["size"]!.GetValue<int>());
        Assert.False(File.Exists(SlotFile + ".upload"));
    }

    [Fact]
    public async Task ASyncedSlotMissingLocallyIsRestoredBeforeLaunch()
    {
        var saves = await InSync();
        File.Delete(SlotFile);

        await saves.SyncBeforeLaunchAsync(_session, _product);

        Assert.Equal(CloudV1, await File.ReadAllBytesAsync(SlotFile));
    }

    [Fact]
    public async Task ADirtySlotWhoseCloudCopyAlsoChangedAsksTheResolverWithTheQualifiedId()
    {
        var saves = await InSync();
        await File.WriteAllTextAsync(SlotFile, "played offline");
        await File.WriteAllBytesAsync(SlotFile + ".upload", []);
        _cloudBytes = Encoding.UTF8.GetBytes("played on another PC");
        _entity = Entity(5, Slot(Md5Hex(_cloudBytes), "2026-11-02T10:00:00Z", _cloudBytes.Length));
        var conflicts = new List<CloudSaveConflict>();
        saves.ConflictResolver = conflict =>
        {
            conflicts.Add(conflict);
            return Task.FromResult(CloudSaveResolution.Skip);
        };

        await saves.SyncBeforeLaunchAsync(_session, _product);

        Assert.Equal("ubisoft:2688", Assert.Single(conflicts).AppName);
        Assert.Equal("played offline", await File.ReadAllTextAsync(SlotFile));
        Assert.True(File.Exists(SlotFile + ".upload"));
        Assert.Empty(_storage);
    }

    private sealed class Signer : IUbisoftCloudSaveSigner
    {
        public readonly List<UbisoftCloudSaveMethod> Methods = [];
        public readonly List<UbisoftCloudSaveItem> Items = [];

        public Task<IReadOnlyList<UbisoftSignedRequest>> SignAsync(UbisoftSession session, uint productId,
            UbisoftCloudSaveMethod method, IReadOnlyList<UbisoftCloudSaveItem> items, CancellationToken ct)
        {
            Methods.Add(method);
            Items.AddRange(items);
            return Task.FromResult<IReadOnlyList<UbisoftSignedRequest>>(items.Select(item => new UbisoftSignedRequest(
                $"https://ubisoft-uplay-savegames.s3.amazonaws.com/{session.ProfileId}/{productId}/{item.Name}.savegame?X-Amz-Signature=test",
                "")).ToList());
        }
    }
}
