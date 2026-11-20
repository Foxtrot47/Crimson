using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Crimson.Providers.Ubisoft.Protocol;

namespace Crimson.Providers.Ubisoft;

public sealed record UbisoftProductDisplay(string Title, string? CoverImageUrl, string? LandscapeImageUrl, string? Description);

public sealed class UbisoftMetadata(Func<string>? locale = null)
{
    private readonly Func<string> _locale = locale ?? (() => CultureInfo.CurrentUICulture.Name);
    private readonly object _sync = new();
    private readonly Dictionary<uint, ParsedConfig> _configs = [];

    public UbisoftProductConfig? Config(UbisoftOwnedProduct product)
    {
        if (string.IsNullOrWhiteSpace(product.Configuration)) return null;
        var locale = _locale();
        lock (_sync)
        {
            if (_configs.TryGetValue(product.ProductId, out var cached) &&
                string.Equals(cached.Locale, locale, StringComparison.Ordinal) &&
                string.Equals(cached.Yaml, product.Configuration, StringComparison.Ordinal))
                return cached.Config;
        }
        UbisoftProductConfig? config;
        try { config = UbisoftProductConfig.Parse(product.Configuration, locale); }
        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException)
        {
            config = null;
        }
        lock (_sync) _configs[product.ProductId] = new ParsedConfig(product.Configuration, locale, config);
        return config;
    }

    public string? Title(UbisoftOwnedProduct product, UbisoftOwnedProduct? parent = null) =>
        NonEmpty(Config(product)?.Name) ?? NonEmpty(ParentEntry(product, parent)?.Name);

    public UbisoftProductDisplay Describe(UbisoftOwnedProduct product, UbisoftOwnedProduct? parent = null)
    {
        var config = Config(product);
        var entry = ParentEntry(product, parent);
        var title = NonEmpty(config?.Name) ?? NonEmpty(entry?.Name) ?? Placeholder(product.ProductId);
        var cover = config?.CoverImage ?? Asset(entry?.CoverImage);
        return config is null
            ? new UbisoftProductDisplay(title, cover, cover, null)
            : new UbisoftProductDisplay(title, cover, config.LandscapeImage, NonEmpty(config.Description));
    }

    public static string Placeholder(uint productId) =>
        "Ubisoft " + productId.ToString(CultureInfo.InvariantCulture);

    private UbisoftConfigAddon? ParentEntry(UbisoftOwnedProduct product, UbisoftOwnedProduct? parent)
    {
        if (parent is null || product.ProductType != UbisoftOwnedProduct.TypeAddon) return null;
        var addons = Config(parent)?.Addons;
        if (addons is null) return null;
        foreach (var addon in addons)
            if (addon.Id == product.ProductId) return addon;
        return null;
    }

    private static string? Asset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? value : UbisoftProductConfig.AssetUrl(value);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParsedConfig(string Yaml, string Locale, UbisoftProductConfig? Config);
}
