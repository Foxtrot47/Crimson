using System.Net;
using System.Text.RegularExpressions;

namespace Crimson.Core;

internal static partial class StoreDescriptionFormatter
{
    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var withLineBreaks = BlockEndPattern().Replace(value, "\n");
        var withoutMarkup = MarkupPattern().Replace(withLineBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutMarkup).Replace("\r", string.Empty);
        var withoutImages = MarkdownImagePattern().Replace(decoded, string.Empty);
        var withLinkText = MarkdownLinkPattern().Replace(withoutImages, "$1");
        var withoutHeadings = MarkdownHeadingPattern().Replace(withLinkText, "$1");
        var withoutEmphasis = MarkdownEmphasisPattern().Replace(withoutHeadings, string.Empty);
        return ExcessWhitespacePattern().Replace(withoutEmphasis, "\n\n").Trim();
    }

    [GeneratedRegex("<(br\\s*/?|/p|/div|/li|/h[1-6])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex("<!--.*?-->|<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex("!\\[[^\\]]*\\]\\([^\\r\\n)]*\\)")]
    private static partial Regex MarkdownImagePattern();

    [GeneratedRegex("\\[([^\\]]+)\\]\\([^\\r\\n)]*\\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex("(?m)^\\s*#{1,6}\\s+(.+)$")]
    private static partial Regex MarkdownHeadingPattern();

    [GeneratedRegex("\\*\\*|__|`")]
    private static partial Regex MarkdownEmphasisPattern();

    [GeneratedRegex("[ \\t]*\\n(?:[ \\t]*\\n){2,}")]
    private static partial Regex ExcessWhitespacePattern();
}
