using System;
using System.Linq;
using Crimson.Models;

namespace Crimson.Core;

public static class EpicStoreLinks
{
    private static readonly Uri StoreUri = new("https://store.epicgames.com/");

    public static Uri? ForGame(Game? game)
    {
        if (game is null) return null;
        var slug = game.Metadata?.CustomAttributes?.ComEpicgamesAppProductSlug?.Value?.Trim().Trim('/');
        if (slug?.EndsWith("/home", StringComparison.OrdinalIgnoreCase) == true)
            slug = slug[..^5];
        if (IsProductSlug(slug)) return CreateProductUri(slug!);
        return string.IsNullOrWhiteSpace(game.AppTitle) ? null : CreateSearchUri(game.AppTitle);
    }

    private static bool IsProductSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length <= 256 &&
        slug.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static Uri CreateSearchUri(string query)
    {
        var escapedQuery = Uri.EscapeDataString(query.Trim());
        return new Uri(StoreUri, $"en-US/browse?q={escapedQuery}&sortBy=relevancy&sortDir=DESC&count=40");
    }

    public static Uri CreateProductUri(string productSlug)
    {
        var escapedSlug = Uri.EscapeDataString(productSlug.Trim('/'));
        return new Uri(StoreUri, $"en-US/p/{escapedSlug}");
    }
}
