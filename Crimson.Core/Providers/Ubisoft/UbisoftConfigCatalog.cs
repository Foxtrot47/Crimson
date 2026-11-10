using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Crimson.Providers.Ubisoft;

/// <summary>Display data for one product taken from the official client's configuration cache.</summary>
public sealed record UbisoftProductMetadata(string Title, string? CoverImageUrl = null,
    string? LandscapeImageUrl = null, string? IconImageUrl = null, uint ParentProductId = 0);

public sealed record UbisoftCatalogAddon(uint ProductId, string Title, string? ThumbImageUrl, bool IsVisible);

/// <summary>How the official client starts the game: a relative exe or a registry-provided one.</summary>
public sealed record UbisoftCatalogExecutable(string? RelativePath, string? PathRegistry,
    string? WorkingDirectoryRegistry, string? ShortcutName);

public sealed record UbisoftCatalogGame(string SpaceId, string Title, string? CoverImageUrl,
    string? LandscapeImageUrl, string? IconImageUrl, IReadOnlyList<UbisoftCatalogAddon> Addons,
    string? GameIdentifier, UbisoftCatalogExecutable? Executable = null, string? Description = null);

/// <summary>
/// Reads names and artwork from the installed Ubisoft Connect client's
/// <c>cache\configuration\configurations</c> file. The file is a tolerant YAML-like dump: every
/// game is a block that starts with <c>version:</c>, entries below <c>root:</c> describe the game,
/// <c>addons:</c> lists its DLC, and <c>localizations.default</c> resolves the <c>lN</c> keys
/// used instead of literal names. Reading it never contacts Ubisoft and never writes to it.
/// </summary>
public sealed partial class UbisoftConfigCatalog
{
    /// <summary>Public artwork host the official client also uses for these hashes.</summary>
    public const string AssetBaseUrl = "https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/";

    private readonly Dictionary<string, UbisoftCatalogGame> _games;
    private readonly Dictionary<uint, (UbisoftCatalogAddon Addon, string SpaceId)> _addons;

    public IReadOnlyCollection<UbisoftCatalogGame> Games => _games.Values;
    public int Count => _games.Count;

    private UbisoftConfigCatalog(Dictionary<string, UbisoftCatalogGame> games,
        Dictionary<uint, (UbisoftCatalogAddon Addon, string SpaceId)> addons)
    {
        _games = games;
        _addons = addons;
    }

    /// <summary>
    /// Parses the configuration catalog streamed by Ubisoft's own service (Demux
    /// <c>client_configuration_service</c>). The service embeds the same YAML text the official client
    /// caches locally, so the reader is identical - only the transport differs.
    /// </summary>
    public static UbisoftConfigCatalog ParseCatalogStream(byte[] stream)
    {
        // The embedded YAML is UTF-8; framing bytes around it decode to replacement characters,
        // which the line reader simply ignores.
        var text = System.Text.Encoding.UTF8.GetString(stream);
        var normalized = text.Replace((char)13, (char)10);
        var lines = normalized.Split((char)10).ToList();
        return Parse(lines);
    }

    public static UbisoftConfigCatalog Parse(IReadOnlyList<string> lines)
    {
        var games = new Dictionary<string, UbisoftCatalogGame>(StringComparer.OrdinalIgnoreCase);
        var addons = new Dictionary<uint, (UbisoftCatalogAddon, string)>();
        var artworkByTitle = new Dictionary<string, UbisoftCatalogGame>(StringComparer.Ordinal);
        var parsed = new List<UbisoftCatalogGame>();
        foreach (var block in SplitBlocks(lines))
        {
            if (ParseBlock(block) is not { } game) continue;
            parsed.Add(game);
            // A block that has real hashes can lend them to the entry that owns the space id.
            if (game.CoverImageUrl is not null || game.LandscapeImageUrl is not null)
                artworkByTitle[TitleKey(game.Title)] = game;
            if (string.IsNullOrWhiteSpace(game.SpaceId)) continue;
            // The dump can describe one game in several blocks; keep everything it says.
            games[game.SpaceId] = games.TryGetValue(game.SpaceId, out var known) ? Merge(known, game) : game;
            foreach (var addon in game.Addons)
            {
                // Test/beta blocks repeat retail addons without their names; keep the named entry.
                if (addons.TryGetValue(addon.ProductId, out var existing) && HasTitle(existing.Item1) && !HasTitle(addon))
                    continue;
                addons[addon.ProductId] = (addon, game.SpaceId);
            }
        }
        foreach (var key in games.Keys.ToList())
        {
            var game = games[key];
            if (game.CoverImageUrl is not null && game.LandscapeImageUrl is not null) continue;
            if (!artworkByTitle.TryGetValue(TitleKey(game.Title), out var donor)) continue;
            games[key] = game with
            {
                CoverImageUrl = game.CoverImageUrl ?? donor.CoverImageUrl,
                LandscapeImageUrl = game.LandscapeImageUrl ?? donor.LandscapeImageUrl,
                IconImageUrl = game.IconImageUrl ?? donor.IconImageUrl,
                Description = game.Description ?? donor.Description
            };
        }
        return new UbisoftConfigCatalog(games, addons);
    }

    /// <summary>Comparison key that ignores quoting, trademarks and pre-order/test-server suffixes.</summary>
    private static string TitleKey(string title)
    {
        var cleaned = Unquote(title);
        foreach (var mark in new[] { "™", "®", "©" }) cleaned = cleaned.Replace(mark, "", StringComparison.Ordinal);
        foreach (var suffix in new[] { " PREORDER", " PRE-ORDER", " PTS", " TEST SERVER" })
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^suffix.Length];
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Trim().ToUpperInvariant();
    }

    public UbisoftCatalogGame? FindGame(string? spaceId) =>
        !string.IsNullOrWhiteSpace(spaceId) && _games.TryGetValue(spaceId, out var game) ? game : null;

    public UbisoftCatalogAddon? FindAddon(uint productId) =>
        _addons.TryGetValue(productId, out var addon) ? addon.Addon : null;

    /// <summary>Space of the game the addon belongs to, or null when the catalog does not know it.</summary>
    public string? ParentSpaceId(uint addonProductId) =>
        _addons.TryGetValue(addonProductId, out var addon) ? addon.SpaceId : null;

    private static UbisoftCatalogGame Merge(UbisoftCatalogGame known, UbisoftCatalogGame next)
    {
        var addons = known.Addons
            .Concat(next.Addons)
            .GroupBy(addon => addon.ProductId)
            .Select(group => group.FirstOrDefault(HasTitle) is { } named && HasTitle(named) ? named : group.First())
            .ToList();
        var title = !IsPlaceholder(known.Title) ? known.Title : IsPlaceholder(next.Title) ? known.Title : next.Title;
        var description = string.IsNullOrWhiteSpace(known.Description) ? next.Description : known.Description;
        return new UbisoftCatalogGame(known.SpaceId, title,
            known.CoverImageUrl ?? next.CoverImageUrl,
            known.LandscapeImageUrl ?? next.LandscapeImageUrl,
            known.IconImageUrl ?? next.IconImageUrl,
            addons,
            known.GameIdentifier ?? next.GameIdentifier,
            known.Executable ?? next.Executable,
            description);
    }

    private static bool IsPlaceholder(string title) =>
        title.StartsWith("Ubisoft game ", StringComparison.Ordinal);

    private static bool HasTitle(UbisoftCatalogAddon addon) => !addon.Title.StartsWith("Ubisoft addon ", StringComparison.Ordinal);

    private static IEnumerable<List<string>> SplitBlocks(IReadOnlyList<string> lines)
    {
        var block = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (BlockStart().IsMatch(line) && !line.StartsWith(' ') && block.Count > 0)
            {
                yield return block;
                block = [];
            }
            block.Add(line);
        }
        if (block.Count > 0) yield return block;
    }

    [GeneratedRegex(@"version\s*:")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^\s*l\d+\s*$")]
    private static partial Regex LocalizationKey();

    [GeneratedRegex(@"^[0-9a-f]{32}\.[A-Za-z0-9]{2,5}$")]
    private static partial Regex AssetHash();

    private static UbisoftCatalogGame? ParseBlock(List<string> block)
    {
        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        var localization = new Dictionary<string, string>(StringComparer.Ordinal);
        var addonEntries = new List<Dictionary<string, string>>();
        var addons = new List<UbisoftCatalogAddon>();
        string? spaceId = null, gameIdentifier = null, firstExecutable = null;
        Dictionary<string, string>? addon = null;
        var section = "";
        // The first start_game executable is the game itself; later entries are tools (map editor…).
        string? executableRelative = null, executableRegistry = null, installRegistry = null, executableShortcut = null;
        var inExecutables = false;
        var capturedExecutable = false;
        var executableField = "";

        foreach (var line in block)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;

            if (indent == 0)
            {
                // A block's sections are declared at column 0, even when the dump is malformed.
                section = trimmed.StartsWith("root:") ? "root"
                    : trimmed.StartsWith("addons:") ? "addons"
                    : trimmed.StartsWith("localizations:") ? "localizations"
                    : "";
                addon = null;
                inExecutables = false;
                continue;
            }

            if (section == "root")
            {
                if (trimmed.StartsWith("executables:", StringComparison.Ordinal)) { inExecutables = true; continue; }
                if (trimmed.StartsWith("start_game:", StringComparison.Ordinal)) { inExecutables = false; continue; }
                if (inExecutables)
                {
                    if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                    {
                        if (capturedExecutable) { inExecutables = false; continue; }
                        // The first key sits on the dash line itself, e.g. "- path:".
                        var inline = trimmed.Length > 2 ? trimmed[2..] : "";
                        executableField = inline.StartsWith("path:", StringComparison.Ordinal) ? "path"
                            : inline.StartsWith("working_directory:", StringComparison.Ordinal) ? "workdir"
                            : "";
                        continue;
                    }
                    // A nested key such as evil_updater: owns its own path; stop reading the current one,
                    // and never replace a path the entry already declared.
                    if (trimmed.EndsWith(':') && !trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        var pathCaptured = executableRelative is not null || executableRegistry is not null;
                        executableField = !pathCaptured && trimmed.StartsWith("path:", StringComparison.Ordinal) ? "path"
                            : trimmed.StartsWith("working_directory:", StringComparison.Ordinal) ? "workdir"
                            : "";
                        continue;
                    }
                    if (trimmed.StartsWith("relative:", StringComparison.Ordinal))
                    {
                        if (executableField == "path" && executableRelative is null)
                            executableRelative = Value(trimmed);
                        continue;
                    }
                    if (trimmed.StartsWith("register:", StringComparison.Ordinal))
                    {
                        if (executableField == "path" && executableRegistry is null) executableRegistry = Value(trimmed);
                        else if (executableField == "workdir" && installRegistry is null) installRegistry = Value(trimmed);
                        continue;
                    }
                    if (trimmed.StartsWith("shortcut_name:", StringComparison.Ordinal) && executableShortcut is null)
                    {
                        executableShortcut = Value(trimmed);
                        capturedExecutable = true;
                        continue;
                    }
                }
            }

            // A block's own space id may be written at two or four spaces.
            if (trimmed.StartsWith("space_id:", StringComparison.Ordinal))
            {
                spaceId ??= Value(trimmed);
                continue;
            }
            if (trimmed.StartsWith("game_identifier:", StringComparison.Ordinal))
            {
                gameIdentifier ??= Value(trimmed);
                continue;
            }
            if (trimmed.StartsWith("shortcut_name:", StringComparison.Ordinal))
            {
                firstExecutable ??= Value(trimmed);
                continue;
            }

            if ((indent == 0 || indent == 2) && trimmed.StartsWith("addons:", StringComparison.Ordinal))
            {
                section = "addons";
                addon = null;
                continue;
            }
            if ((indent == 0 || indent == 2) && trimmed.StartsWith("localizations:", StringComparison.Ordinal))
            {
                section = "localizations";
                addon = null;
                continue;
            }
            if ((indent == 0 || indent == 2) && trimmed.StartsWith("root:", StringComparison.Ordinal))
            {
                section = "root";
                addon = null;
                continue;
            }

            if (section == "localizations")
            {
                // Only the default locale is read; other locales repeat the same keys.
                if (indent <= 2)
                    section = trimmed.StartsWith("default:", StringComparison.Ordinal) ? "localizations" : "localizations-other";
                else if (indent >= 4 && trimmed.IndexOf(':') is var separator and > 0)
                    localization[trimmed[..separator]] = Unquote(trimmed[(separator + 1)..]);
                continue;
            }
            if (section == "localizations-other")
            {
                if (indent == 2) section = "";
                else continue;
            }

            if (section == "addons")
            {
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                {
                    addon = new Dictionary<string, string>(StringComparer.Ordinal);
                    addonEntries.Add(addon);
                    var inline = trimmed.Length > 2 ? trimmed[2..] : "";
                    if (inline.IndexOf(':') is var separator and > 0)
                        addon[inline[..separator]] = Unquote(inline[(separator + 1)..]);
                    continue;
                }
                if (addon is not null && indent >= 4 && trimmed.IndexOf(':') is var pair and > 0)
                    addon[trimmed[..pair]] = Unquote(trimmed[(pair + 1)..]);
                continue;
            }

            if (section == "root" && indent == 2 && trimmed.IndexOf(':') is var rootPair and > 0)
                root[trimmed[..rootPair]] = Unquote(trimmed[(rootPair + 1)..]);
        }

        string Field(string key) => root.TryGetValue(key, out var value) ? Resolve(value) : "";

        foreach (var entry in addonEntries)
        {
            if (!entry.TryGetValue("id", out var idText) || !uint.TryParse(idText, out var id) || id == 0) continue;
            entry.TryGetValue("name", out var name);
            entry.TryGetValue("thumb_image", out var thumb);
            var visible = entry.TryGetValue("is_visible", out var flag) && flag.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var title = Resolve(name ?? "");
            if (string.IsNullOrWhiteSpace(title)) title = $"Ubisoft addon {id}";
            addons.Add(new UbisoftCatalogAddon(id, title, AssetUrl(Resolve(thumb ?? "")), visible));
        }

        var gameTitle = Field("name");
        if (string.IsNullOrWhiteSpace(gameTitle) || TemplateToken().IsMatch(gameTitle))
            gameTitle = gameIdentifier ?? firstExecutable ?? "";
        if (string.IsNullOrWhiteSpace(gameTitle)) gameTitle = $"Ubisoft game {spaceId}";

        var executable = executableRelative is null && executableRegistry is null && installRegistry is null
            ? null
            : new UbisoftCatalogExecutable(executableRelative, executableRegistry, installRegistry, executableShortcut);

        var description = Field("description");
        if (string.IsNullOrWhiteSpace(description) && localization.TryGetValue("DESCRIPTION", out var localized))
            description = localized;

        return new UbisoftCatalogGame(spaceId, gameTitle,
            AssetUrl(Field("thumb_image")),
            AssetUrl(Field("background_image")),
            AssetUrl(Field("icon_image")),
            addons, gameIdentifier, executable,
            string.IsNullOrWhiteSpace(description) ? null : description);

        string Resolve(string value) =>
            LocalizationKey().IsMatch(value) && localization.TryGetValue(value, out var text) ? text : value;
    }

    [GeneratedRegex(@"^(GAMENAME|NAME)$")]
    private static partial Regex TemplateToken();

    private static string Value(string line) => Unquote(line[(line.IndexOf(':') + 1)..]);

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length > 1 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static string? AssetUrl(string? hash) =>
        string.IsNullOrWhiteSpace(hash) || !AssetHash().IsMatch(hash) ? null : AssetBaseUrl + hash;
}
