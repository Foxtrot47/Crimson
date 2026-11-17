using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Providers.Ubisoft.Protocol;

namespace Crimson.Providers.Ubisoft;

public interface IUbisoftProductSource
{
    /// <summary>Includes unowned products; null when unknown.</summary>
    Task<UbisoftOwnedProduct?> GetProduct(uint productId, CancellationToken ct);

    /// <summary>Product and uplay ids of owned products, for manifest chunk gating.</summary>
    Task<IReadOnlySet<uint>> GetOwnedIds(CancellationToken ct);

    string? GetTitle(uint productId);
}

/// <summary>CD keys and encryption keys never reach <c>ownership.json</c>.</summary>
public sealed class UbisoftOwnershipCache : IUbisoftProductSource
{
    private const int FormatVersion = 1;
    private static readonly string[] ObsoleteFiles = ["catalog.bin", "catalog.bin.tmp", "manifests.json", "metadata.json"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _file;
    private readonly UbisoftMetadata _metadata;
    private readonly UbisoftAuthentication? _auth;
    private readonly Func<UbisoftSession, CancellationToken, Task<IReadOnlyList<UbisoftOwnedProduct>>> _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state = State.Empty;
    private int _cleaned;

    public UbisoftOwnershipCache(string providerRoot, UbisoftMetadata metadata, UbisoftAuthentication? auth = null)
        : this(providerRoot, metadata, auth, FetchAsync) { }

    internal UbisoftOwnershipCache(string providerRoot, UbisoftMetadata metadata, UbisoftAuthentication? auth,
        Func<UbisoftSession, CancellationToken, Task<IReadOnlyList<UbisoftOwnedProduct>>> fetch)
    {
        ProviderRoot = Path.GetFullPath(providerRoot);
        _file = Path.Combine(ProviderRoot, "ownership.json");
        _metadata = metadata;
        _auth = auth;
        _fetch = fetch;
        if (auth is not null)
            auth.StatusChanged += status =>
            {
                if (status is AuthenticationStatus.LoggedOut or AuthenticationStatus.LoginFailed) Clear();
            };
    }

    public string ProviderRoot { get; }

    public event Action? Changed;

    public IReadOnlyList<UbisoftOwnedProduct> Products => _state.Products;

    /// <summary>The list came from the service during this session, not from the file.</summary>
    public bool IsLive => _state.Live;

    /// <summary>Never contacts Ubisoft.</summary>
    public async Task<IReadOnlyList<UbisoftOwnedProduct>> LoadAsync(string userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state.UserId == userId && _state.Products.Count > 0) return _state.Products;
            DeleteObsoleteFiles();
            var products = await ReadFileAsync(userId, ct);
            _state = new State(userId, products, false);
            return products;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<UbisoftOwnedProduct>> RefreshAsync(UbisoftSession session, CancellationToken ct = default)
    {
        var products = (await _fetch(session, ct)).ToList();
        await _gate.WaitAsync(ct);
        try
        {
            DeleteObsoleteFiles();
            _state = new State(session.UserId, products, true);
            await WriteFileAsync(session.UserId, products, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return products;
    }

    /// <summary>The file stays: it is keyed by account and holds no secrets.</summary>
    public void Clear() => _state = State.Empty;

    public async Task<UbisoftOwnedProduct?> GetProduct(uint productId, CancellationToken ct)
    {
        var state = await EnsureAsync(ct);
        if (state.ById.TryGetValue(productId, out var product)) return product;
        // A product bought since the list was cached is only known to the service.
        if (state.Live || _auth is null) return null;
        state = await RefreshCurrentAsync(ct);
        return state.ById.GetValueOrDefault(productId);
    }

    public async Task<IReadOnlySet<uint>> GetOwnedIds(CancellationToken ct)
    {
        var state = await EnsureAsync(ct);
        var owned = new HashSet<uint>();
        foreach (var product in state.Products.Where(product => product.Owned))
        {
            owned.Add(product.ProductId);
            if (product.UplayId != 0) owned.Add(product.UplayId);
        }
        return owned;
    }

    public string? GetTitle(uint productId)
    {
        var state = _state;
        if (!state.ById.TryGetValue(productId, out var product)) return null;
        return _metadata.Title(product, ParentOf(product, state.Products));
    }

    internal UbisoftOwnedProduct? ParentOf(UbisoftOwnedProduct product, IReadOnlyList<UbisoftOwnedProduct> products)
    {
        if (product.ProductType != UbisoftOwnedProduct.TypeAddon) return null;
        var games = products.Where(game => game.ProductType == UbisoftOwnedProduct.TypeGame && game.ProductId != product.ProductId)
            .OrderByDescending(game => game.Owned).ToList();
        return games.FirstOrDefault(game => _metadata.Config(game)?.Addons.Any(addon => addon.Id == product.ProductId) == true)
            ?? games.FirstOrDefault(game => game.Associations.Contains(product.ProductId));
    }

    private async Task<State> EnsureAsync(CancellationToken ct)
    {
        var state = _state;
        if (state.Products.Count > 0 || _auth is null) return state;
        var session = await _auth.RequireSession(ct);
        await LoadAsync(session.UserId, ct);
        if (_state.Products.Count > 0) return _state;
        return await RefreshCurrentAsync(ct);
    }

    private async Task<State> RefreshCurrentAsync(CancellationToken ct)
    {
        var session = await _auth!.RequireSession(ct);
        await RefreshAsync(session, ct);
        return _state;
    }

    private static async Task<IReadOnlyList<UbisoftOwnedProduct>> FetchAsync(UbisoftSession session, CancellationToken ct)
    {
        await using var connection = await UbisoftDemuxConnection.ConnectAsync(session.Ticket, ct);
        var ownership = await UbisoftOwnershipService.OpenAsync(connection, ct);
        return ownership.Products;
    }

    private async Task<IReadOnlyList<UbisoftOwnedProduct>> ReadFileAsync(string userId, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_file)) return [];
            await using var stream = File.OpenRead(_file);
            var file = await JsonSerializer.DeserializeAsync<CacheFile>(stream, SerializerOptions, ct);
            if (file is null || file.Version != FormatVersion || file.UserId != userId || file.Products is null) return [];
            return file.Products.Where(product => product.ProductId != 0).Select(product => product.ToProduct()).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task WriteFileAsync(string userId, IReadOnlyList<UbisoftOwnedProduct> products, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ProviderRoot);
            var temporary = _file + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, new CacheFile(FormatVersion, userId, DateTimeOffset.UtcNow,
                    products.Select(CachedProduct.From).ToList()), SerializerOptions, ct);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The list stays usable in memory; the next refresh writes again.
        }
    }

    private void DeleteObsoleteFiles()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        foreach (var name in ObsoleteFiles)
        {
            try { File.Delete(Path.Combine(ProviderRoot, name)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record State(string? UserId, IReadOnlyList<UbisoftOwnedProduct> Products, bool Live)
    {
        public static State Empty { get; } = new(null, [], false);
        public IReadOnlyDictionary<uint, UbisoftOwnedProduct> ById { get; } =
            Products.GroupBy(product => product.ProductId).ToDictionary(group => group.Key, group => group.Last());
    }

    private sealed record CacheFile(int Version, string UserId, DateTimeOffset UpdatedAt, List<CachedProduct>? Products);

    /// <summary>The persisted shape, listed field by field so nothing secret can be added by accident.</summary>
    private sealed record CachedProduct(uint ProductId, bool Owned, uint ProductType, uint State, uint UplayId, uint DownloadId,
        string? Configuration, string? LatestManifest, uint ActiveBranchId, List<CachedBranch>? Branches, List<uint>? Associations,
        string? SpaceId, string? AppId, uint ActivationType, uint PackageOwnershipState, uint Balance, uint TargetPartner,
        bool LockedBySubscription, uint SuspensionType)
    {
        public static CachedProduct From(UbisoftOwnedProduct p) => new(p.ProductId, p.Owned, p.ProductType, p.State, p.UplayId,
            p.DownloadId, p.Configuration, p.LatestManifest, p.ActiveBranchId,
            p.Branches.Select(branch => new CachedBranch(branch.Id, branch.Name)).ToList(), p.Associations.ToList(),
            p.SpaceId, p.AppId, p.ActivationType, p.PackageOwnershipState, p.Balance, p.TargetPartner,
            p.LockedBySubscription, p.SuspensionType);

        public UbisoftOwnedProduct ToProduct() => new(ProductId, Owned, ProductType, State, UplayId, DownloadId,
            Configuration, LatestManifest, ActiveBranchId,
            (Branches ?? []).Select(branch => new UbisoftProductBranch(branch.Id, branch.Name ?? "")).ToList(),
            Associations ?? [], SpaceId, AppId, ActivationType, PackageOwnershipState, Balance, TargetPartner,
            LockedBySubscription, SuspensionType);
    }

    private sealed record CachedBranch(uint Id, string? Name);
}
