using System.Text.Json;
using Crimson.Models;

namespace Crimson.Infrastructure;

public static class JsonStateSchemas
{
    public static JsonStateSchema<UserData> Credentials { get; } =
        new("credentials", 1, 1024 * 1024);

    public static JsonStateSchema<List<Asset>> GameAssets { get; } =
        new("game-assets", 1, 32L * 1024 * 1024);

    public static JsonStateSchema<Game> GameMetadata { get; } =
        new("game-metadata", 1, 16L * 1024 * 1024);

    public static JsonStateSchema<Dictionary<string, LocalAppState>> LocalInstallations { get; } =
        new("local-installations", 1, 16L * 1024 * 1024);

    public static JsonStateSchema<Settings> Settings { get; } =
        new("settings", 2, 1024 * 1024, DeserializeSettings);

    public static JsonStateSchema<string> InstallOperationStateJson { get; } =
        new("install-operation-state", 1, 64L * 1024 * 1024, DeserializeNestedJson);

    public static JsonStateSchema<Dictionary<string, string>> ManifestIndex { get; } =
        new("manifest-index", 1, 16L * 1024 * 1024);

    public static IReadOnlyList<JsonStateCategoryDescriptor> Catalog { get; } =
    [
        Describe(Credentials, "user.json", "Windows credential compatibility store", true,
            "Transitional encrypted credential record; Phase 6 moves the implementation behind the Windows credential adapter."),
        Describe(GameAssets, "assets.json", "Library state owner", false,
            "Regenerable Epic asset cache."),
        Describe(GameMetadata, "metadata/<app-key>.json", "Library state owner", false,
            "One bounded metadata cache record per canonical app key."),
        Describe(LocalInstallations, "localstate.json", "Installation state owner", true,
            "Recognized installations and installed manifest identity."),
        Describe(Settings, "settings.json", "Settings state owner", true,
            "Typed settings; schema 2 migrates raw JSON and the schema-1 nested JSON string."),
        Describe(InstallOperationStateJson, "install_state.json", "InstallManager compatibility owner", true,
            "Transitional nested operation JSON; Phase 8 replaces it with the typed operation journal."),
        Describe(ManifestIndex, "manifests/index.json", "Manifest cache owner", false,
            "Regenerable content-addressed manifest lookup index.")
    ];

    private static JsonStateCategoryDescriptor Describe<T>(
        JsonStateSchema<T> schema,
        string relativePath,
        string owner,
        bool authoritative,
        string notes) => new(
            schema.Category,
            relativePath,
            owner,
            schema.CurrentVersion,
            schema.MaximumBytes,
            authoritative,
            notes);

    private static Settings? DeserializeSettings(
        int version,
        JsonElement data,
        JsonSerializerOptions? options) => version switch
    {
        0 or 2 => data.Deserialize<Settings>(options),
        1 when data.ValueKind == JsonValueKind.String =>
            JsonSerializer.Deserialize<Settings>(data.GetString() ?? string.Empty, options),
        1 => data.Deserialize<Settings>(options),
        _ => throw new JsonException($"Unsupported settings schema version {version}.")
    };

    private static string? DeserializeNestedJson(
        int version,
        JsonElement data,
        JsonSerializerOptions? _) => version switch
    {
        0 => data.GetRawText(),
        1 when data.ValueKind == JsonValueKind.String => data.GetString(),
        1 => data.GetRawText(),
        _ => throw new JsonException($"Unsupported install-state schema version {version}.")
    };
}
