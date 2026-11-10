using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Resolves names and artwork for owned products and caches the result.
///
/// The entitlement endpoint returns identifiers only. Display data comes from the official
/// client's configuration cache when it is installed; anything still unnamed is resolved once
/// through the applications endpoint and remembered in <c>metadata.json</c>. Both inputs are
/// public product data, so the cache is shared between accounts.
/// </summary>
public sealed class UbisoftMetadata(UbisoftApi api, string providerRoot,
    Func<UbisoftSession, CancellationToken, Task<byte[]>>? fetchCatalog = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _file = Path.Combine(providerRoot, "metadata.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UbisoftConfigCatalog? _catalog;
    private string _catalogStamp = "";
    private Dictionary<uint, CachedProduct> _products = [];
    private Dictionary<string, string> _spaceTitles = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _catalogFile = Path.Combine(providerRoot, "catalog.bin");
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromHours(12);
    private readonly Func<UbisoftSession, CancellationToken, Task<byte[]>> _fetchCatalog =
        fetchCatalog ?? ((session, token) => new UbisoftConfigCatalogClient().FetchAsync(session, token));

    /// <summary>How the current catalog was obtained, for diagnostics only.</summary>
    public string? CatalogSource { get; private set; }

    /// <summary>Display and launch data the official client recorded for a space, when it knows it.</summary>
    public UbisoftCatalogGame? FindCatalogGame(string? spaceId) => _catalog?.FindGame(spaceId);

    public UbisoftCatalogAddon? FindCatalogAddon(uint productId) => _catalog?.FindAddon(productId);

    public async Task<IReadOnlyDictionary<uint, UbisoftProductMetadata>> Resolve(UbisoftSession session,
        IReadOnlyList<UbisoftEntitlement> entitlements, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await EnsureCatalogAsync(session, token);
            await LoadCache(token);
            var changed = await ResolveMissingTitles(session, entitlements, token);
            var resolved = Project(entitlements);
            if (changed) await SaveCacheAsync(token);
            return resolved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Loads the configuration catalog from Ubisoft's service, reusing a cached copy while it is
    /// fresh. No official client is required: the same data is served by Demux.
    /// </summary>
    private async Task EnsureCatalogAsync(UbisoftSession session, CancellationToken token)
    {
        if (_catalog is not null) return;
        if (await TryLoadCachedCatalogAsync(token)) return;
        try
        {
            var stream = await _fetchCatalog(session, token);
            _catalog = UbisoftConfigCatalog.ParseCatalogStream(stream);
            _catalogStamp = "service|" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CatalogSource = "ubisoft service";
            await SaveCatalogAsync(stream, token);
        }
        catch (Exception error) when (error is UbisoftApiException or System.Net.Http.HttpRequestException
            or IOException or OperationCanceledException)
        {
            // Without the catalog the library still lists owned games; only display data is missing.
            _catalogStamp = "";
            CatalogSource = null;
        }
    }

    private async Task<bool> TryLoadCachedCatalogAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(_catalogFile)) return false;
            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_catalogFile) > CatalogLifetime) return false;
            _catalog = UbisoftConfigCatalog.ParseCatalogStream(await File.ReadAllBytesAsync(_catalogFile, token));
            _catalogStamp = "cache|" + File.GetLastWriteTimeUtc(_catalogFile).Ticks;
            CatalogSource = "cached service data";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task SaveCatalogAsync(byte[] stream, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(providerRoot);
            var temporary = _catalogFile + ".tmp";
            await File.WriteAllBytesAsync(temporary, stream, token);
            File.Move(temporary, _catalogFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private async Task LoadCache(CancellationToken token)
    {
        if (!File.Exists(_file)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_file, token));
            var root = document.RootElement;
            // An empty source means the file predates catalog tracking: it must not discard a live catalog.
            if (root.TryGetProperty("source", out var source) && source.GetString() is { Length: > 0 } stored &&
                stored != _catalogStamp)
            {
                // A different catalog produced these titles: re-resolve display data against it.
                _catalog = null;
                _catalogStamp = stored;
            }
            if (root.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Object)
                foreach (var entry in titles.EnumerateObject())
                    if (entry.Value.GetString() is { } title) _spaceTitles[entry.Name] = title;
            if (root.TryGetProperty("addons", out var addons) && addons.ValueKind == JsonValueKind.Object)
                foreach (var entry in addons.EnumerateObject())
                    if (uint.TryParse(entry.Name, out var id) && entry.Value.GetString() is { } title)
                        _products[id] = new CachedProduct(title, null, null, 0);
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private async Task<bool> ResolveMissingTitles(UbisoftSession session, IReadOnlyList<UbisoftEntitlement> entitlements,
        CancellationToken token)
    {
        var missing = entitlements
            .Where(entitlement => entitlement.Type == "game" && entitlement.ApplicationId is not null &&
                string.IsNullOrWhiteSpace(ResolveSpaceTitle(entitlement.SpaceId)) &&
                string.IsNullOrWhiteSpace(entitlement.Title) &&
                _catalog?.FindGame(entitlement.SpaceId) is null)
            .ToList();
        if (missing.Count == 0) return false;

        var changed = false;
        foreach (var chunk in missing.Select((entitlement, index) => (entitlement, index)).Chunk(20))
        {
            var applications = string.Join(',', chunk.Select(pair => pair.entitlement.ApplicationId));
            JsonElement json;
            try
            {
                json = await api.Get(session,
                    "/v1/applications?applicationIds=" + Uri.EscapeDataString(applications), token);
            }
            catch (UbisoftApiException) { return changed; } // Names stay unavailable; ids remain usable.
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("applications", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var application in list.EnumerateArray())
            {
                if (!application.TryGetProperty("spaceId", out var space) || space.GetString() is not { } spaceId) continue;
                if (!application.TryGetProperty("name", out var name) || name.GetString() is not { } title) continue;
                if (string.IsNullOrWhiteSpace(title)) continue;
                _spaceTitles[spaceId] = title;
                changed = true;
            }
        }
        return changed;
    }

    private Dictionary<uint, UbisoftProductMetadata> Project(IReadOnlyList<UbisoftEntitlement> entitlements)
    {
        // Parents are expressed as a space in the client cache and as a product id in ownership.
        var productBySpace = entitlements
            .Where(entitlement => !string.IsNullOrWhiteSpace(entitlement.SpaceId))
            .GroupBy(entitlement => entitlement.SpaceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ProductId, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<uint, UbisoftProductMetadata>(entitlements.Count);
        foreach (var entitlement in entitlements)
        {
            var game = _catalog?.FindGame(entitlement.SpaceId);
            var addon = entitlement.Type == "addon" ? _catalog?.FindAddon(entitlement.ProductId) : null;
            var parentSpace = entitlement.Type == "addon" ? _catalog?.ParentSpaceId(entitlement.ProductId) : null;
            var parent = parentSpace is not null && productBySpace.TryGetValue(parentSpace, out var parentId) ? parentId : 0;

            var cachedTitle = _products.TryGetValue(entitlement.ProductId, out var cached) ? cached.Title : null;
            var title = FirstNonEmpty(entitlement.Title, addon?.Title, game?.Title,
                _spaceTitles.GetValueOrDefault(entitlement.SpaceId ?? ""), cachedTitle);
            if (string.IsNullOrWhiteSpace(title))
                title = $"Ubisoft {entitlement.Type} {entitlement.ProductId}";

            result[entitlement.ProductId] = new UbisoftProductMetadata(title,
                addon?.ThumbImageUrl ?? game?.CoverImageUrl,
                game?.LandscapeImageUrl ?? addon?.ThumbImageUrl,
                game?.IconImageUrl,
                parent);
        }
        return result;
    }

    private string? ResolveSpaceTitle(string? spaceId) =>
        string.IsNullOrWhiteSpace(spaceId) ? null : _spaceTitles.GetValueOrDefault(spaceId);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private async Task SaveCacheAsync(CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(providerRoot);
            var titles = _spaceTitles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(new MetadataCacheFile(_catalogStamp, titles), SerializerOptions);
            var temporary = _file + ".tmp";
            await File.WriteAllTextAsync(temporary, json, token);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record MetadataCacheFile(string Source, Dictionary<string, string> Titles);
    private sealed record CachedProduct(string Title, string? CoverImageUrl, string? LandscapeImageUrl, uint ParentProductId);
}
