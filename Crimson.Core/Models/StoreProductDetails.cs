using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Crimson.Models;

public sealed class StoreProductDetails
{
    public string? ProductName { get; init; }
    public string? DeveloperName { get; init; }
    public string? PublisherName { get; init; }
    public string? ShortDescription { get; init; }
    public string? LongDescription { get; init; }
    public string? WebsiteUrl { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public IReadOnlyList<StoreProductImage> Images { get; init; } = [];
    public IReadOnlyList<StoreProductTag> Tags { get; init; } = [];
    public IReadOnlyList<string> SupportedAudioLanguages { get; init; } = [];
    public IReadOnlyList<string> SupportedTextLanguages { get; init; } = [];
    public IReadOnlyList<StoreSystemRequirement> WindowsRequirements { get; init; } = [];
}

public sealed class StoreProductImage
{
    public required string Type { get; init; }
    public required string Url { get; init; }
    public string? AltText { get; init; }
    public bool IsVideo { get; init; }
}

public sealed class StoreProductTag
{
    public required string Name { get; init; }
    public string? GroupName { get; init; }
}

public sealed class StoreSystemRequirement
{
    public required string Title { get; init; }
    public string? Minimum { get; init; }
    public string? Recommended { get; init; }
}

internal static class StoreProductDetailsParser
{
    public static StoreProductDetails? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetConfiguration(document.RootElement, out var configurations))
                return null;

            var store = FindConfiguration(configurations, "StoreConfiguration");
            var home = FindConfiguration(configurations, "HomeConfiguration");
            if (store is null && home is null)
                return null;

            return CreateDetails(store, home);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StoreProductDetails CreateDetails(JsonElement? store, JsonElement? home)
    {
        var storeConfig = GetConfig(store);
        var homeConfig = GetConfig(home);
        return new StoreProductDetails
        {
            ProductName = GetString(storeConfig, "productDisplayName"),
            DeveloperName = GetString(storeConfig, "developerDisplayName"),
            PublisherName = GetString(storeConfig, "publisherDisplayName"),
            ShortDescription = GetString(storeConfig, "shortDescription"),
            LongDescription = GetString(homeConfig, "longDescription"),
            WebsiteUrl = GetString(storeConfig, "gameWebsite"),
            ReleaseDate = GetDate(storeConfig, "pcReleaseDate"),
            Images = ParseImages(storeConfig, homeConfig),
            Tags = ParseTags(storeConfig),
            SupportedAudioLanguages = ParseStrings(storeConfig, "supportedAudio"),
            SupportedTextLanguages = ParseStrings(storeConfig, "supportedText"),
            WindowsRequirements = ParseRequirements(storeConfig)
        };
    }

    private static bool TryGetConfiguration(JsonElement root, out JsonElement configurations)
    {
        configurations = default;
        return TryGetProperty(root, "data", out var data) &&
            TryGetProperty(data, "Product", out var product) &&
            TryGetProperty(product, "sandbox", out var sandbox) &&
            TryGetProperty(sandbox, "configuration", out configurations) &&
            configurations.ValueKind == JsonValueKind.Array;
    }

    private static JsonElement? FindConfiguration(JsonElement configurations, string type)
    {
        foreach (var configuration in configurations.EnumerateArray())
        {
            if (string.Equals(GetString(configuration, "__typename"), type, StringComparison.Ordinal))
                return configuration;
        }

        return null;
    }

    private static JsonElement? GetConfig(JsonElement? configuration) =>
        configuration is { } value && TryGetProperty(value, "configs", out var config)
            ? config
            : null;

    private static IReadOnlyList<StoreProductImage> ParseImages(
        JsonElement? storeConfig,
        JsonElement? homeConfig)
    {
        var images = new List<StoreProductImage>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddImages(storeConfig, images, urls);
        AddImages(homeConfig, images, urls);
        return images;
    }

    private static void AddImages(
        JsonElement? config,
        ICollection<StoreProductImage> images,
        ISet<string> urls)
    {
        if (config is not { } value ||
            !TryGetProperty(value, "keyImages", out var keyImages) ||
            keyImages.ValueKind != JsonValueKind.Array)
            return;

        foreach (var image in keyImages.EnumerateArray())
        {
            var type = GetString(image, "type");
            var url = GetString(image, "url");
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(url) || !urls.Add(url))
                continue;

            images.Add(new StoreProductImage
            {
                Type = type,
                Url = url,
                AltText = GetString(image, "alt"),
                IsVideo = string.Equals(GetString(image, "__typename"), "Video", StringComparison.Ordinal)
            });
        }
    }

    private static IReadOnlyList<StoreProductTag> ParseTags(JsonElement? storeConfig)
    {
        if (storeConfig is not { } value ||
            !TryGetProperty(value, "tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<StoreProductTag>();
        foreach (var tag in tags.EnumerateArray())
        {
            var name = GetString(tag, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new StoreProductTag
            {
                Name = name,
                GroupName = GetString(tag, "groupName")
            });
        }

        return results;
    }

    private static IReadOnlyList<string> ParseStrings(JsonElement? config, string propertyName)
    {
        if (config is not { } value ||
            !TryGetProperty(value, propertyName, out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return [];

        return items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static IReadOnlyList<StoreSystemRequirement> ParseRequirements(JsonElement? storeConfig)
    {
        if (storeConfig is not { } value ||
            !TryGetProperty(value, "technicalRequirements", out var technicalRequirements) ||
            !TryGetProperty(technicalRequirements, "windows", out var windows) ||
            windows.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<StoreSystemRequirement>();
        foreach (var requirement in windows.EnumerateArray())
        {
            var title = GetString(requirement, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            results.Add(new StoreSystemRequirement
            {
                Title = title,
                Minimum = GetString(requirement, "minimum"),
                Recommended = GetString(requirement, "recommended")
            });
        }

        return results;
    }

    private static DateTimeOffset? GetDate(JsonElement? element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }

    private static string? GetString(JsonElement? element, string propertyName) =>
        element is { } value &&
        TryGetProperty(value, propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
            return true;

        property = default;
        return false;
    }
}
