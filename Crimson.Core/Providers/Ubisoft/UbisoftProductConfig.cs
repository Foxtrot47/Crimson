using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// One <c>start_game.online|offline.executables[]</c> entry. The executable is <c>path.relative</c> under the install
/// directory, or the directory read from <c>path.register</c> plus <c>path.append</c>; the working directory is
/// resolved the same way from <c>working_directory</c>.
/// </summary>
public sealed record UbisoftExecutableEntry(string? RelativePath, string? RegisterPath, string? RegisterAppend,
    string? WorkingDirectoryRelative, string? WorkingDirectoryRegister, string? WorkingDirectoryAppend,
    string? Arguments, string? ShortcutName, bool Denuvo);

public sealed record UbisoftConfigAddon(uint Id, string? Name, bool IsVisible, string? CoverImage);

/// <summary>
/// The YAML product configuration the ownership service returns per product. Values such as <c>l1</c> or
/// <c>GAMENAME</c> are keys into <c>localizations.&lt;locale&gt;</c>, falling back to <c>localizations.default</c>.
/// Unknown keys are ignored; malformed lines are skipped.
/// </summary>
public sealed class UbisoftProductConfig
{
    public const int MaximumLength = 4 * 1024 * 1024;
    private const string AssetBaseUrl = "https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/";

    private UbisoftProductConfig() { }

    public string? Name { get; private init; }
    public string? SortName { get; private init; }
    public string? Description { get; private init; }
    public string? CoverImage { get; private init; }
    public string? LandscapeImage { get; private init; }
    public string? LogoImage { get; private init; }
    public string? IconImage { get; private init; }
    public IReadOnlyList<UbisoftExecutableEntry> OnlineExecutables { get; private init; } = [];
    public IReadOnlyList<UbisoftExecutableEntry> OfflineExecutables { get; private init; } = [];
    public bool CloudSaves { get; private init; }
    public bool ChangeLanguageEnabled { get; private init; }
    public string? GameIdentifier { get; private init; }
    public string? SpaceId { get; private init; }
    public string? AppId { get; private init; }
    public string? ThirdPartyPlatform { get; private init; }
    public IReadOnlyList<UbisoftConfigAddon> Addons { get; private init; } = [];

    /// <param name="locale">UI culture such as <c>fr-FR</c>; matched exactly, then by language prefix. Null uses <c>default</c>.</param>
    public static UbisoftProductConfig Parse(string yaml, string? locale = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (yaml.Length > MaximumLength) throw new InvalidDataException("Ubisoft product configuration is too large.");
        var document = Yaml.Parse(yaml) as YamlMap;
        var root = document?.Get("root") as YamlMap ?? new YamlMap();
        var localization = new Localization(document?.Get("localizations") as YamlMap, locale);
        var startGame = root.Get("start_game") as YamlMap;
        return new UbisoftProductConfig
        {
            Name = localization.Text(root, "name"),
            SortName = localization.Text(root, "sort_string"),
            Description = localization.Text(root, "description"),
            CoverImage = Image(localization.Text(root, "thumb_image")),
            LandscapeImage = Image(localization.Text(root, "background_image")),
            LogoImage = Image(localization.Text(root, "logo_image")),
            IconImage = Image(localization.Text(root, "icon_image")),
            OnlineExecutables = Executables(startGame?.Get("online") as YamlMap, localization),
            OfflineExecutables = Executables(startGame?.Get("offline") as YamlMap, localization),
            CloudSaves = Flag(root.Get("cloud_saves")),
            ChangeLanguageEnabled = Flag(root.Get("change_language_enabled")),
            GameIdentifier = Scalar((root.Get("installer") as YamlMap)?.Get("game_identifier")),
            SpaceId = CrashReportingFallback(root, "space_id"),
            AppId = CrashReportingFallback(root, "app_id"),
            ThirdPartyPlatform = PlatformName(root.Get("third_party_platform")),
            Addons = ParseAddons(root.Get("addons") as List<object?>, localization),
        };
    }

    public static string AssetUrl(string hashWithExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashWithExtension);
        return AssetBaseUrl + hashWithExtension.Trim();
    }

    /// <summary>A root value, else the same key under <c>crash_reporting</c>.</summary>
    private static string? CrashReportingFallback(YamlMap root, string key) =>
        Scalar(root.Get(key)) ?? Scalar((root.Get("crash_reporting") as YamlMap)?.Get(key));

    /// <summary><c>third_party_platform</c> is either a scalar or a mapping with a <c>name</c>.</summary>
    private static string? PlatformName(object? node) => node is YamlMap platform ? Scalar(platform.Get("name")) : Scalar(node);

    private static IReadOnlyList<UbisoftExecutableEntry> Executables(YamlMap? mode, Localization localization)
    {
        if (mode?.Get("executables") is not List<object?> executables) return [];
        var entries = new List<UbisoftExecutableEntry>();
        foreach (var item in executables)
            if (item is YamlMap executable && Executable(executable, localization) is { } entry) entries.Add(entry);
        return entries;
    }

    private static UbisoftExecutableEntry? Executable(YamlMap executable, Localization localization)
    {
        var path = executable.Get("path");
        var pathMap = path as YamlMap;
        var relative = pathMap is null ? Scalar(path) : Scalar(pathMap.Get("relative"));
        var register = Scalar(pathMap?.Get("register"));
        if (relative is null && register is null) return null;
        var workingDirectory = executable.Get("working_directory") as YamlMap;
        return new UbisoftExecutableEntry(
            relative,
            register,
            Scalar(pathMap?.Get("append")),
            Scalar(workingDirectory?.Get("relative")),
            Scalar(workingDirectory?.Get("register")),
            Scalar(workingDirectory?.Get("append")),
            Scalar(executable.Get("arguments")),
            localization.Text(executable, "shortcut_name"),
            Flag(executable.Get("denuvo")));
    }

    private static IReadOnlyList<UbisoftConfigAddon> ParseAddons(List<object?>? addons, Localization localization)
    {
        if (addons is null) return [];
        var result = new List<UbisoftConfigAddon>();
        foreach (var item in addons)
        {
            if (item is not YamlMap addon) continue;
            if (!uint.TryParse(Scalar(addon.Get("id")), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var visible = addon.Get("is_visible");
            result.Add(new UbisoftConfigAddon(id, localization.Text(addon, "name"),
                visible is null || Flag(visible), Image(localization.Text(addon, "thumb_image"))));
        }
        return result;
    }

    /// <summary>Asset file names become CDN URLs; absolute URLs stay only on the Ubisoft artwork host; unresolved
    /// placeholders are dropped.</summary>
    private static string? Image(string? value)
    {
        if (value is null) return null;
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return UbisoftContentPolicy.AllowsArtworkUri(value) ? value : null;
        var dot = value.LastIndexOf('.');
        if (dot <= 0 || dot == value.Length - 1) return null;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return null;
        return AssetUrl(value);
    }

    private static string? Scalar(object? node)
    {
        if (node is not string text) return null;
        text = text.Trim();
        return text.Length == 0 || text is "~" or "null" ? null : text;
    }

    private static bool Flag(object? node) =>
        Scalar(node)?.ToLowerInvariant() is "yes" or "true" or "on" or "y" or "1";

    private sealed class Localization
    {
        private readonly YamlMap? _default;
        private readonly YamlMap? _locale;

        public Localization(YamlMap? localizations, string? locale)
        {
            _default = localizations?.Get("default") as YamlMap;
            if (localizations is null || string.IsNullOrWhiteSpace(locale)) return;
            locale = locale.Trim().Replace('_', '-');
            var codes = localizations.Keys.Where(code => !string.Equals(code, "default", StringComparison.Ordinal)).ToArray();
            var language = locale.Split('-')[0];
            var match = codes.FirstOrDefault(code => string.Equals(code, locale, StringComparison.OrdinalIgnoreCase))
                ?? codes.FirstOrDefault(code => string.Equals(code, $"{language}-{language}", StringComparison.OrdinalIgnoreCase))
                ?? codes.FirstOrDefault(code => string.Equals(code.Split('-')[0], language, StringComparison.OrdinalIgnoreCase));
            if (match is not null) _locale = localizations.Get(match) as YamlMap;
        }

        public string? Text(YamlMap node, string key)
        {
            var value = Scalar(node.Get(key));
            if (value is null) return null;
            return Scalar(_locale?.Get(value)) ?? Scalar(_default?.Get(value)) ?? value;
        }
    }

    /// <summary>A mapping that keeps document order; a repeated key keeps its last value.</summary>
    private sealed class YamlMap
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        public List<string> Keys { get; } = [];
        public object? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, object? value)
        {
            if (!_values.ContainsKey(key)) Keys.Add(key);
            _values[key] = value;
        }
    }

    /// <summary>
    /// Block-style YAML subset used by Ubisoft configurations: nested mappings and sequences by indentation, plain,
    /// single- and double-quoted scalars (multi-line folded), literal/folded block scalars, and single-line flow
    /// collections without nesting. Anything else is skipped rather than rejected.
    /// </summary>
    private sealed class Yaml
    {
        private const int MaximumDepth = 64;
        private readonly string[] _raw;
        private readonly int[] _indent;
        private readonly string[] _text;
        private int _position;

        private Yaml(string yaml)
        {
            _raw = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            _indent = new int[_raw.Length];
            _text = new string[_raw.Length];
            for (var index = 0; index < _raw.Length; index++)
            {
                var line = _raw[index];
                var indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                _indent[index] = indent;
                _text[index] = line[indent..].TrimEnd();
            }
        }

        public static object? Parse(string yaml)
        {
            if (yaml.Length > 0 && yaml[0] == '\uFEFF') yaml = yaml[1..];
            var parser = new Yaml(yaml);
            return parser.SkipBlank() ? parser.Block(-1, 0) : null;
        }

        private bool SkipBlank()
        {
            while (_position < _raw.Length)
            {
                var text = _text[_position];
                if (text.Length > 0 && text[0] != '#' && !(_indent[_position] == 0 && (text is "---" or "..." || text[0] == '%')))
                    return true;
                _position++;
            }
            return false;
        }

        private object? Block(int parentIndent, int depth)
        {
            if (!SkipBlank() || _indent[_position] <= parentIndent) return null;
            if (depth >= MaximumDepth)
            {
                while (SkipBlank() && _indent[_position] > parentIndent) _position++;
                return null;
            }
            var indent = _indent[_position];
            return IsSequenceItem(_text[_position]) ? Sequence(indent, depth) : Mapping(indent, depth);
        }

        private YamlMap Mapping(int indent, int depth)
        {
            var map = new YamlMap();
            while (SkipBlank() && _indent[_position] >= indent)
            {
                var text = _text[_position];
                if (_indent[_position] == indent && IsSequenceItem(text)) break;
                if (_indent[_position] > indent || !SplitKey(text, out var key, out var rest))
                {
                    _position++; // stray line: tolerate rather than fail the whole configuration
                    continue;
                }
                _position++;
                map.Set(key, Value(rest, indent, depth, sameIndentSequence: true));
            }
            return map;
        }

        private List<object?> Sequence(int indent, int depth)
        {
            var list = new List<object?>();
            while (SkipBlank() && _indent[_position] >= indent)
            {
                var text = _text[_position];
                if (_indent[_position] > indent)
                {
                    _position++;
                    continue;
                }
                if (!IsSequenceItem(text)) break;
                var after = text[1..];
                var content = after.TrimStart();
                if (content.Length == 0 || content[0] == '#')
                {
                    _position++;
                    list.Add(Value(string.Empty, indent, depth, sameIndentSequence: false));
                }
                else if (IsSequenceItem(content) || SplitKey(content, out _, out _))
                {
                    // "- key: value" opens a mapping whose column is the content's column.
                    _indent[_position] = indent + 1 + (after.Length - content.Length);
                    _text[_position] = content;
                    list.Add(Block(indent, depth + 1));
                }
                else
                {
                    _position++;
                    list.Add(Value(content, indent, depth, sameIndentSequence: false));
                }
            }
            return list;
        }

        private object? Value(string rest, int indent, int depth, bool sameIndentSequence)
        {
            if (rest.Length == 0 || rest[0] == '#')
            {
                if (!SkipBlank()) return null;
                if (_indent[_position] > indent) return Block(indent, depth + 1);
                if (sameIndentSequence && _indent[_position] == indent && IsSequenceItem(_text[_position]))
                    return depth + 1 >= MaximumDepth ? null : Sequence(indent, depth + 1);
                return null;
            }
            return rest[0] switch
            {
                '|' or '>' when IsBlockHeader(rest) => BlockScalar(rest, indent),
                '"' or '\'' => Quoted(rest, indent),
                '[' or '{' => Flow(rest),
                _ => Plain(rest, indent),
            };
        }

        private string Plain(string rest, int indent)
        {
            var builder = new StringBuilder(StripComment(rest));
            while (_position < _raw.Length)
            {
                var text = _text[_position];
                if (text.Length == 0) { _position++; continue; }
                if (_indent[_position] <= indent || text[0] == '#' || SplitKey(text, out _, out _) || IsSequenceItem(text)) break;
                builder.Append(' ').Append(StripComment(text));
                _position++;
            }
            return builder.ToString().Trim();
        }

        private string Quoted(string rest, int indent)
        {
            var quote = rest[0];
            var builder = new StringBuilder();
            if (Unquote(rest.AsSpan(1), quote, builder)) return builder.ToString();
            while (_position < _raw.Length)
            {
                var text = _text[_position];
                if (text.Length == 0)
                {
                    builder.Append('\n');
                    _position++;
                    continue;
                }
                if (_indent[_position] <= indent) break; // unterminated: keep what was read
                if (builder.Length > 0 && builder[^1] != '\n') builder.Append(' ');
                _position++;
                if (Unquote(text, quote, builder)) break;
            }
            return builder.ToString();
        }

        private static bool Unquote(ReadOnlySpan<char> text, char quote, StringBuilder builder)
        {
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character == quote)
                {
                    if (!IsDoubledSingleQuote(text, index, quote)) return true;
                    builder.Append('\'');
                    index++;
                }
                else if (quote == '"' && character == '\\' && index + 1 < text.Length)
                    index += 1 + AppendEscape(text, index + 1, builder);
                else
                    builder.Append(character);
            }
            return false;
        }

        private static bool IsDoubledSingleQuote(ReadOnlySpan<char> text, int index, char quote) =>
            quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'';

        private static int AppendEscape(ReadOnlySpan<char> text, int index, StringBuilder builder)
        {
            switch (text[index])
            {
                case 'n': builder.Append('\n'); return 0;
                case 't': builder.Append('\t'); return 0;
                case 'r': builder.Append('\r'); return 0;
                case '0': builder.Append('\0'); return 0;
                case 'x' when TryHex(text, index + 1, 2, out var code): builder.Append((char)code); return 2;
                case 'u' when TryHex(text, index + 1, 4, out var code): builder.Append((char)code); return 4;
                case 'U' when TryHex(text, index + 1, 8, out var code) && IsScalarValue(code):
                    builder.Append(char.ConvertFromUtf32(code)); return 8;
                default: builder.Append(text[index]); return 0;
            }
        }

        // Not Rune.IsValid: an 8-digit hex escape can parse negative, which must still reach ConvertFromUtf32.
        private static bool IsScalarValue(int code) => code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF);

        private static bool TryHex(ReadOnlySpan<char> text, int start, int length, out int value)
        {
            value = 0;
            return start + length <= text.Length
                && int.TryParse(text.Slice(start, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        private string BlockScalar(string header, int indent)
        {
            var lines = BlockLines(indent);
            var value = (header[0] == '>' ? Fold(lines) : string.Join('\n', lines)).TrimEnd('\n');
            return header.Contains('-') || value.Length == 0 ? value : value + "\n";
        }

        private List<string> BlockLines(int indent)
        {
            var lines = new List<string>();
            var blockIndent = -1;
            while (_position < _raw.Length)
            {
                if (_text[_position].Length == 0)
                {
                    lines.Add(string.Empty);
                    _position++;
                    continue;
                }
                if (blockIndent < 0)
                {
                    if (_indent[_position] <= indent) break;
                    blockIndent = _indent[_position];
                }
                if (_indent[_position] < blockIndent) break;
                lines.Add(_raw[_position][blockIndent..].TrimEnd());
                _position++;
            }
            return lines;
        }

        private static string Fold(List<string> lines)
        {
            var builder = new StringBuilder();
            var separate = false;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    builder.Append('\n');
                    separate = false;
                    continue;
                }
                if (separate) builder.Append(line[0] == ' ' ? '\n' : ' ');
                builder.Append(line);
                separate = true;
            }
            return builder.ToString();
        }

        private static object Flow(string rest)
        {
            var text = StripComment(rest);
            var close = text[0] == '[' ? ']' : '}';
            var inner = text.Length >= 2 && text[^1] == close ? text[1..^1] : text[1..];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (close == ']') return parts.Select(part => (object?)Scalar(part)).ToList();
            var map = new YamlMap();
            foreach (var part in parts)
            {
                var separator = part.IndexOf(':');
                if (separator > 0) map.Set(Scalar(part[..separator]), Scalar(part[(separator + 1)..]));
            }
            return map;
        }

        private static string Scalar(string text)
        {
            text = text.Trim();
            if (text.Length >= 2 && (text[0] is '"' or '\''))
            {
                var builder = new StringBuilder();
                Unquote(text.AsSpan(1), text[0], builder);
                return builder.ToString();
            }
            return text;
        }

        private static bool IsSequenceItem(string text) => text.Length > 0 && text[0] == '-' && (text.Length == 1 || text[1] == ' ');

        private static bool IsBlockHeader(string text)
        {
            var header = StripComment(text);
            for (var index = 1; index < header.Length; index++)
                if (header[index] is not ('-' or '+') && !char.IsAsciiDigit(header[index])) return false;
            return true;
        }

        private static bool SplitKey(string text, out string key, out string rest)
        {
            key = rest = string.Empty;
            if (!MayStartKey(text)) return false;
            var colon = text[0] is '"' or '\'' ? QuotedKey(text, out key) : PlainKey(text, out key);
            if (colon < 0) return false;
            rest = text[(colon + 1)..].Trim();
            return true;
        }

        private static bool MayStartKey(string text) =>
            text.Length > 0 && text[0] is not ('#' or '[' or '{' or '|' or '>') && !IsSequenceItem(text);

        private static int QuotedKey(string text, out string key)
        {
            key = string.Empty;
            var closing = ClosingQuote(text);
            if (closing < 0) return -1;
            var colon = closing + 1;
            while (colon < text.Length && text[colon] == ' ') colon++;
            if (!IsKeyColon(text, colon)) return -1;
            var builder = new StringBuilder();
            Unquote(text.AsSpan(1), text[0], builder);
            key = builder.ToString();
            return colon;
        }

        private static bool IsKeyColon(string text, int colon) =>
            colon < text.Length && text[colon] == ':' && (colon + 1 == text.Length || text[colon + 1] == ' ');

        private static int PlainKey(string text, out string key)
        {
            key = string.Empty;
            var colon = text.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (text[^1] != ':') return -1;
                colon = text.Length - 1;
            }
            key = text[..colon].TrimEnd();
            return key.Length == 0 ? -1 : colon;
        }

        private static int ClosingQuote(string text)
        {
            var quote = text[0];
            for (var index = 1; index < text.Length; index++)
            {
                if (quote == '"' && text[index] == '\\') { index++; continue; }
                if (text[index] != quote) continue;
                if (quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'') { index++; continue; }
                return index;
            }
            return -1;
        }

        private static string StripComment(string text)
        {
            for (var index = 1; index < text.Length; index++)
                if (text[index] == '#' && (text[index - 1] == ' ' || text[index - 1] == '\t')) return text[..index].TrimEnd();
            return text;
        }
    }
}
