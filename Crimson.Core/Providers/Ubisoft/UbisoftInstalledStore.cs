using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Providers.Ubisoft;

public sealed record UbisoftInstalledGame
{
    public required uint ProductId { get; init; }

    /// <summary>Absolute, normalized, no trailing separator.</summary>
    public required string InstallPath { get; init; }

    /// <summary>40-hex id of the installed build; null for an import not verified yet.</summary>
    public string? ManifestId { get; init; }

    public string? GameVersion { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<uint> ChunkIds { get; init; } = [];

    /// <summary>"identifier:version" of every manifest install run already applied.</summary>
    public IReadOnlyList<string> CompletedInstallRuns { get; init; } = [];

    /// <summary>Registry values and prerequisites were not applied yet (the elevation prompt was declined).</summary>
    public bool SystemSetupPending { get; init; }

    /// <summary>A verification found damaged or missing files; a repair clears it.</summary>
    public bool NeedsRepair { get; init; }

    public ulong InstallBytes { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Installed Ubisoft products in <c>providers/ubisoft/installed.json</c>. Writes are atomic and serialized;
/// reads after the first load are served from memory.
/// </summary>
public sealed class UbisoftInstalledStore(string providerRoot)
{
    private const string FileName = "installed.json";
    private const string LegacyFileName = "configuration.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _root = Path.GetFullPath(providerRoot);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<uint, UbisoftInstalledGame> _games = [];
    private volatile bool _loaded;

    public event Action<uint>? Changed;

    public string ProviderRoot => _root;

    public async Task<IReadOnlyList<UbisoftInstalledGame>> LoadAsync(CancellationToken ct = default)
    {
        if (!_loaded)
        {
            await _gate.WaitAsync(ct);
            try { if (!_loaded) await LoadLockedAsync(ct); }
            finally { _gate.Release(); }
        }
        lock (_sync) return _games.Values.OrderBy(game => game.ProductId).ToList();
    }

    public UbisoftInstalledGame? Find(uint productId)
    {
        lock (_sync) return _games.GetValueOrDefault(productId);
    }

    public async Task SaveAsync(UbisoftInstalledGame game, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.ProductId == 0) throw new ArgumentException("A Ubisoft product id is required.", nameof(game));
        var normalized = game with { InstallPath = NormalizePath(game.InstallPath) };
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded) await LoadLockedAsync(ct);
            Dictionary<uint, UbisoftInstalledGame> next;
            lock (_sync) next = new(_games) { [normalized.ProductId] = normalized };
            await WriteLockedAsync(next.Values, ct);
            lock (_sync) _games = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(normalized.ProductId);
    }

    public async Task RemoveAsync(uint productId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded) await LoadLockedAsync(ct);
            Dictionary<uint, UbisoftInstalledGame> next;
            lock (_sync)
            {
                if (!_games.ContainsKey(productId)) return;
                next = new(_games);
            }
            next.Remove(productId);
            await WriteLockedAsync(next.Values, ct);
            lock (_sync) _games = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(productId);
    }

    /// <summary>Absolute and without a trailing separator, so paths compare and combine predictably.</summary>
    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute installation path.", nameof(path));
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        // A drive root keeps its separator: "C:" alone means the drive's current directory.
        return Path.GetPathRoot(full) is { } root && string.Equals(root, full, StringComparison.OrdinalIgnoreCase)
            ? full : trimmed;
    }

    private async Task LoadLockedAsync(CancellationToken ct)
    {
        var file = Path.Combine(_root, FileName);
        Dictionary<uint, UbisoftInstalledGame> games = [];
        if (File.Exists(file))
        {
            await using var stream = File.OpenRead(file);
            var stored = await JsonSerializer.DeserializeAsync<StoredFile>(stream, Json, ct);
            foreach (var game in stored?.Games ?? [])
                if (game.ProductId != 0 && Path.IsPathFullyQualified(game.InstallPath)) games[game.ProductId] = game;
        }
        else if (MigrateLegacy() is { Count: > 0 } migrated)
        {
            foreach (var game in migrated) games[game.ProductId] = game;
            await WriteLockedAsync(games.Values, ct);
        }
        lock (_sync) _games = games;
        _loaded = true;
    }

    /// <summary>
    /// One-time import of the old <c>configuration.json</c> "Games" list. Those installs were never checked
    /// against a manifest, so they come over without a build id and the library offers to update them.
    /// </summary>
    private List<UbisoftInstalledGame> MigrateLegacy()
    {
        var file = Path.Combine(_root, LegacyFileName);
        var games = new List<UbisoftInstalledGame>();
        if (!File.Exists(file)) return games;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Games", out var list) || list.ValueKind != JsonValueKind.Array)
                return games;
            var installedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            foreach (var entry in list.EnumerateArray())
            {
                if (!TryReadLegacyGame(entry, out var productId, out var path)) continue;
                if (games.Any(game => game.ProductId == productId)) continue;
                games.Add(new UbisoftInstalledGame
                {
                    ProductId = productId,
                    InstallPath = NormalizePath(path),
                    Languages = LegacyLanguages(entry),
                    InstalledAt = installedAt
                });
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable legacy file holds nothing worth blocking the library for.
        }
        return games;
    }

    private static bool TryReadLegacyGame(JsonElement entry, out uint productId, out string path)
    {
        productId = 0;
        path = "";
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("ProductId", out var id) || !id.TryGetUInt32(out productId) || productId == 0 ||
            !entry.TryGetProperty("InstallDirectory", out var directory) ||
            directory.ValueKind != JsonValueKind.String || directory.GetString() is not { } value)
            return false;
        path = value;
        return Path.IsPathFullyQualified(path);
    }

    private static IReadOnlyList<string> LegacyLanguages(JsonElement entry)
    {
        var language = entry.TryGetProperty("Language", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(language) ? [] : [language];
    }

    private async Task WriteLockedAsync(IEnumerable<UbisoftInstalledGame> games, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, FileName);
        var temporary = file + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new StoredFile(1, games.OrderBy(game => game.ProductId).ToList()), Json, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record StoredFile(int Version, List<UbisoftInstalledGame> Games);
}
