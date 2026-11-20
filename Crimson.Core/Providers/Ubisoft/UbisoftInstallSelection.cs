using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Crimson.Providers.Ubisoft;

public static class UbisoftInstallSelection
{
    private const string FallbackLanguage = "en-US";

    /// <summary>
    /// Files of every chunk that passes the gates, de-duplicated by path (case-insensitive). The same file listed by
    /// two chunks is merged when both entries agree and rejected when they disagree.
    /// </summary>
    public static IReadOnlyList<UbisoftManifestFile> Select(UbisoftManifest manifest, IReadOnlyCollection<string> languages, IReadOnlySet<uint> owned)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(owned);
        var files = new List<UbisoftManifestFile>();
        var byPath = new Dictionary<string, UbisoftManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in manifest.Chunks)
        {
            if (!Passes(chunk.UplayIds, chunk.Language, languages, owned)) continue;
            foreach (var file in chunk.Files)
            {
                if (byPath.TryGetValue(file.Path, out var existing))
                {
                    if (!SameContent(existing, file))
                        throw new InvalidDataException($"Ubisoft manifest lists '{file.Path}' twice with different content.");
                    continue;
                }
                byPath.Add(file.Path, file);
                files.Add(file);
            }
        }
        return files;
    }

    public static IReadOnlyList<string> AvailableLanguages(UbisoftManifest manifest, IReadOnlySet<uint> owned)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(owned);
        return Available(manifest.Languages, manifest.Chunks.Select(chunk => (chunk.UplayIds, chunk.Language)), owned);
    }

    public static IReadOnlyList<string> AvailableLanguages(UbisoftManifestMetadata metadata, IReadOnlySet<uint> owned)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(owned);
        return Available(metadata.Languages, metadata.Chunks.Select(chunk => (chunk.UplayIds, chunk.Language)), owned);
    }

    /// <summary>
    /// The language matching <paramref name="uiCulture"/> exactly, then one with the same language prefix, then en-US,
    /// then the first available. With nothing available, en-US (language-less content installs regardless).
    /// </summary>
    public static string DefaultLanguage(IReadOnlyList<string> available, string uiCulture)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0) return FallbackLanguage;
        var culture = uiCulture?.Trim() ?? string.Empty;
        if (culture.Length > 0)
        {
            var exact = available.FirstOrDefault(code => string.Equals(code, culture, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var prefix = LanguagePrefix(culture);
            var sameLanguage = available.FirstOrDefault(code => string.Equals(LanguagePrefix(code), prefix, StringComparison.OrdinalIgnoreCase));
            if (sameLanguage is not null) return sameLanguage;
        }
        return available.FirstOrDefault(code => string.Equals(code, FallbackLanguage, StringComparison.OrdinalIgnoreCase)) ?? available[0];
    }

    internal static bool Passes(IReadOnlyList<uint> uplayIds, string? language, IReadOnlyCollection<string> languages, IReadOnlySet<uint> owned)
    {
        if (uplayIds.Count > 0 && !uplayIds.Any(owned.Contains)) return false;
        return string.IsNullOrEmpty(language)
            || languages.Any(selected => string.Equals(selected, language, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Available(IReadOnlyList<UbisoftManifestLanguage> declared,
        IEnumerable<(IReadOnlyList<uint> UplayIds, string? Language)> chunks, IReadOnlySet<uint> owned)
    {
        var codes = declared.Count > 0
            ? declared.Where(language => language.UplayIds.Count == 0 || language.UplayIds.Any(owned.Contains)).Select(language => language.Code)
            : chunks.Where(chunk => !string.IsNullOrEmpty(chunk.Language) && (chunk.UplayIds.Count == 0 || chunk.UplayIds.Any(owned.Contains)))
                .Select(chunk => chunk.Language!);
        return codes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string LanguagePrefix(string code)
    {
        var separator = code.IndexOfAny(['-', '_']);
        return separator < 0 ? code : code[..separator];
    }

    private static bool SameContent(UbisoftManifestFile left, UbisoftManifestFile right)
    {
        if (left.Size != right.Size || left.IsDirectory != right.IsDirectory || left.Slices.Count != right.Slices.Count) return false;
        for (var index = 0; index < left.Slices.Count; index++)
        {
            var a = left.Slices[index];
            var b = right.Slices[index];
            if (a.Size != b.Size || a.Offset != b.Offset || !string.Equals(a.DownloadSha1, b.DownloadSha1, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
