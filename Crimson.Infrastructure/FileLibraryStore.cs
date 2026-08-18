using Crimson.Core;
using Crimson.Models;

namespace Crimson.Infrastructure;

public sealed class FileLibraryStore : ILibraryStore
{
    private readonly string _appDataRoot;
    private readonly object _writeGate = new();

    public FileLibraryStore(string appDataRoot)
    {
        _appDataRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(appDataRoot)
                ? throw new ArgumentException("Application data root is required.", nameof(appDataRoot))
                : appDataRoot);
        Directory.CreateDirectory(_appDataRoot);
        Directory.CreateDirectory(Path.Combine(_appDataRoot, "metadata"));
    }

    public LibraryStoreState Read()
    {
        var assetsResult = AtomicJsonFile.ReadAndMigrate(
            Path.Combine(_appDataRoot, "assets.json"),
            JsonStateSchemas.GameAssets);
        var assets = assetsResult.Status switch
        {
            JsonStateReadStatus.Success => assetsResult.Value ?? [],
            JsonStateReadStatus.Missing => [],
            JsonStateReadStatus.UnsupportedVersion => throw new NotSupportedException(
                $"Game asset schema version {assetsResult.Version} is not supported."),
            _ => throw new InvalidDataException(
                $"Game asset state is corrupt: {assetsResult.Error ?? "unknown error"}.")
        };
        return new LibraryStoreState(assets, ReadGames(), ReadLocalStates());
    }

    public LibraryStoreState WriteRefresh(
        IReadOnlyList<Asset> assets,
        IReadOnlyList<Game> games,
        IReadOnlyList<LibraryInstallationRefresh> installationRefreshes)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(installationRefreshes);
        lock (_writeGate)
        {
            AtomicJsonFile.Write(
                Path.Combine(_appDataRoot, "assets.json"),
                assets.ToList(),
                JsonStateSchemas.GameAssets);
            var metadataRoot = Path.Combine(_appDataRoot, "metadata");
            var incoming = games.Select(game => game.AppName).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in ReadGames().Keys)
            {
                if (incoming.Contains(existing))
                    continue;
                var stalePath = Path.Combine(metadataRoot, $"{StorageKeyCodec.Encode(existing)}.json");
                File.Delete(stalePath);
                File.Delete(stalePath + ".bak");
            }
            foreach (var game in games)
            {
                AtomicJsonFile.Write(
                    Path.Combine(metadataRoot, $"{StorageKeyCodec.Encode(game.AppName)}.json"),
                    game,
                    JsonStateSchemas.GameMetadata);
            }

            var localInstallations = ReadLocalStates().ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            foreach (var refresh in installationRefreshes)
            {
                if (!localInstallations.TryGetValue(refresh.AppName, out var local))
                    continue;
                local.AvailableManifestDigest = refresh.AvailableManifestDigest;
                var classification = GameUpdateClassifier.Classify(local, refresh.AssetBuildVersion);
                if (local.InstallStatus is InstallState.Installed or InstallState.NeedUpdate)
                {
                    local.InstallStatus = classification == GameUpdateClassification.UpdateAvailable
                        ? InstallState.NeedUpdate
                        : InstallState.Installed;
                }
            }
            AtomicJsonFile.Write(
                Path.Combine(_appDataRoot, "localstate.json"),
                localInstallations,
                JsonStateSchemas.LocalInstallations);
            return new LibraryStoreState(
                assets,
                games.ToDictionary(game => game.AppName, StringComparer.Ordinal),
                localInstallations);
        }
    }

    private IReadOnlyDictionary<string, Game> ReadGames()
    {
        var result = new Dictionary<string, Game>(StringComparer.Ordinal);
        var metadataRoot = Path.Combine(_appDataRoot, "metadata");
        foreach (var path in Directory.EnumerateFiles(metadataRoot, "*.json"))
        {
            var read = AtomicJsonFile.ReadAndMigrate(path, JsonStateSchemas.GameMetadata);
            if (read.Status == JsonStateReadStatus.UnsupportedVersion)
                throw new NotSupportedException(
                    $"Game metadata schema version {read.Version} is not supported.");
            if (!read.IsSuccess || read.Value is null)
                continue;
            var expectedName = $"{StorageKeyCodec.Encode(read.Value.AppName)}.json";
            if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                continue;
            result[read.Value.AppName] = read.Value;
        }
        return result;
    }

    private IReadOnlyDictionary<string, LocalAppState> ReadLocalStates()
    {
        var result = AtomicJsonFile.ReadAndMigrate(
            Path.Combine(_appDataRoot, "localstate.json"),
            JsonStateSchemas.LocalInstallations);
        return result.Status switch
        {
            JsonStateReadStatus.Success => result.Value
                ?? new Dictionary<string, LocalAppState>(StringComparer.Ordinal),
            JsonStateReadStatus.Missing => new Dictionary<string, LocalAppState>(StringComparer.Ordinal),
            JsonStateReadStatus.UnsupportedVersion => throw new NotSupportedException(
                $"Local installation schema version {result.Version} is not supported."),
            _ => throw new InvalidDataException(
                $"Local installation state is corrupt: {result.Error ?? "unknown error"}.")
        };
    }
}

public static class AppDataPaths
{
    public static string GetDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Crimson");

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(xdgDataHome, "Crimson")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "Crimson");
    }
}
