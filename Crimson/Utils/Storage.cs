using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Infrastructure;
using Crimson.Models;
using Serilog;

namespace Crimson.Utils
{
    public class Storage : ICredentialStore
    {
        private readonly string _appDataPath;
        private readonly string _userDataFile;
        private readonly string _gameAssetsFile;
        private readonly string _metaDataDirectory;
        private readonly string _installationStateFile;
        private readonly string _localAppStateFile;
        private readonly string _manifestPath;
        private readonly string _manifestIndexFile;
        private readonly ConcurrentDictionary<string, Game> _gameMetaDataDictionary;
        private readonly ConcurrentDictionary<string, LocalAppState> _localAppStateDictionary;
        private readonly ConcurrentDictionary<string, string> _manifestIndex;
        private IReadOnlyDictionary<string, Game> _gameMetadataSnapshot =
            FrozenDictionary<string, Game>.Empty;
        private IReadOnlyDictionary<string, LocalAppState> _localAppStateSnapshot =
            FrozenDictionary<string, LocalAppState>.Empty;
        private readonly object _writeLock = new();
        private readonly ILogger _logger;

        public IReadOnlyDictionary<string, Game> GameMetaDataDictionary =>
            Volatile.Read(ref _gameMetadataSnapshot);
        public IReadOnlyDictionary<string, LocalAppState> LocalAppStateDictionary =>
            Volatile.Read(ref _localAppStateSnapshot);
        public string DefaultInstallPath => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);


        public Storage() : this(App.GetService<ILogger>())
        {
        }

        public Storage(ILogger logger, string? appDataPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appDataPath = Path.GetFullPath(appDataPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Crimson"));
            _userDataFile = ResolveAppDataPath("user.json");
            _gameAssetsFile = ResolveAppDataPath("assets.json");
            _metaDataDirectory = ResolveAppDataPath("metadata");
            _installationStateFile = ResolveAppDataPath("install_state.json");
            _localAppStateFile = ResolveAppDataPath("localstate.json");
            _manifestPath = ResolveAppDataPath("manifests");
            _manifestIndexFile = ResolveAppDataPath("manifests", "index.json");

            Directory.CreateDirectory(_metaDataDirectory);
            Directory.CreateDirectory(_manifestPath);
            _gameMetaDataDictionary = new ConcurrentDictionary<string, Game>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_metaDataDirectory, "*.json"))
            {
                try
                {
                    var result = AtomicJsonFile.Read(file, JsonStateSchemas.GameMetadata);
                    if (!result.IsSuccess || result.Value is null)
                    {
                        if (result.Status != JsonStateReadStatus.Missing)
                            _logger.Warning(
                                "Skipped metadata state with {Status}: {Error}",
                                result.Status,
                                result.Error);
                        continue;
                    }
                    var game = result.Value;

                    var canonicalFileName = $"{StorageKeyCodec.Encode(game.AppName)}.json";
                    if (!string.Equals(Path.GetFileName(file), canonicalFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (result.Version != JsonStateSchemas.GameMetadata.CurrentVersion ||
                        result.Source == JsonStateSource.Backup)
                    {
                        game = AtomicJsonFile.ReadAndMigrate(file, JsonStateSchemas.GameMetadata).Value
                            ?? throw new InvalidDataException("Migrated metadata state was empty.");
                    }

                    _gameMetaDataDictionary[game.AppName] = game;
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        "Failed to load metadata file {FileName} with {ErrorType}",
                        Path.GetFileName(file),
                        exception.GetType().Name);
                }
            }

            var localStates = ReadState(
                _localAppStateFile,
                JsonStateSchemas.LocalInstallations,
                authoritative: true);
            _localAppStateDictionary = localStates is not null
                ? new ConcurrentDictionary<string, LocalAppState>(localStates, StringComparer.Ordinal)
                : new ConcurrentDictionary<string, LocalAppState>(StringComparer.Ordinal);
            var manifestIndex = ReadState(
                _manifestIndexFile,
                JsonStateSchemas.ManifestIndex,
                authoritative: false);
            _manifestIndex = manifestIndex is not null
                ? new ConcurrentDictionary<string, string>(manifestIndex, StringComparer.Ordinal)
                : new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            PublishGameMetadataSnapshot();
            PublishLocalAppStateSnapshot();
        }

        public Task<UserData?> GetUserData() =>
            Task.FromResult(ReadState(
                _userDataFile,
                JsonStateSchemas.Credentials,
                authoritative: false));

        public Task SaveUserData(UserData? data)
        {
            lock (_writeLock)
            {
                if (data is null)
                {
                    File.Delete(_userDataFile);
                    File.Delete(_userDataFile + ".bak");
                }
                else
                {
                    AtomicJsonFile.Write(_userDataFile, data, JsonStateSchemas.Credentials);
                }
            }

            return Task.CompletedTask;
        }

        public Task ClearUserData()
        {
            try
            {
                lock (_writeLock)
                {
                    File.Delete(_userDataFile);
                    File.Delete(_userDataFile + ".bak");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to clear user data file");
            }

            return Task.CompletedTask;
        }

        public Task<IEnumerable<Asset>?> GetGameAssetsData() =>
            Task.FromResult<IEnumerable<Asset>?>(ReadState(
                _gameAssetsFile,
                JsonStateSchemas.GameAssets,
                authoritative: false));

        public Task SaveGameAssetsData(IEnumerable<Asset>? data)
        {
            try
            {
                lock (_writeLock)
                {
                    if (data is null)
                    {
                        File.Delete(_gameAssetsFile);
                        File.Delete(_gameAssetsFile + ".bak");
                    }
                    else
                    {
                        AtomicJsonFile.Write(_gameAssetsFile, data.ToList(), JsonStateSchemas.GameAssets);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save game assets");
            }

            return Task.CompletedTask;
        }

        public Game GetGameMetaData(string gameName)
        {
            if (!_gameMetaDataDictionary.TryGetValue(gameName, out var game)) return null;
            HydrateLocalAppState(game);
            return game;
        }

        /// <summary>
        /// Hydrate Game.LocalAppState from the LocalAppStateDictionary if not already set
        /// </summary>
        private void HydrateLocalAppState(Game game)
        {
            if (game.LocalAppState == null && _localAppStateDictionary.TryGetValue(game.AppName, out var localState))
                game.LocalAppState = localState;
        }

        /// <summary>
        /// Hydrate LocalAppState for all games in the metadata dictionary
        /// </summary>
        public void HydrateAllLocalAppStates()
        {
            foreach (var game in _gameMetaDataDictionary.Values)
            {
                HydrateLocalAppState(game);
            }
        }

        public void SaveMetaData(Game game)
        {
            ArgumentNullException.ThrowIfNull(game);
            var fileName = ResolveAppDataPath("metadata", $"{StorageKeyCodec.Encode(game.AppName)}.json");
            lock (_writeLock)
            {
                AtomicJsonFile.Write(fileName, game, JsonStateSchemas.GameMetadata);
                _gameMetaDataDictionary[game.AppName] = game;
                PublishGameMetadataSnapshot();
            }
        }

        public void UpdateLocalAppState(IReadOnlyDictionary<string, LocalAppState> installedGames)
        {
            ArgumentNullException.ThrowIfNull(installedGames);
            lock (_writeLock)
            {
                _localAppStateDictionary.Clear();
                foreach (var (appName, appState) in installedGames)
                    _localAppStateDictionary[appName] = appState;
                AtomicJsonFile.Write(
                    _localAppStateFile,
                    _localAppStateDictionary.ToDictionary(pair => pair.Key, pair => pair.Value),
                    JsonStateSchemas.LocalInstallations);
                PublishLocalAppStateSnapshot();
            }
        }

        public void AddToLocalAppState(string appName, LocalAppState appState)
        {
            lock (_writeLock)
            {
                _localAppStateDictionary[appName] = appState;
                AtomicJsonFile.Write(
                    _localAppStateFile,
                    _localAppStateDictionary.ToDictionary(pair => pair.Key, pair => pair.Value),
                    JsonStateSchemas.LocalInstallations);
                PublishLocalAppStateSnapshot();
            }
        }


        public void SaveInstallState(string data)
        {
            lock (_writeLock)
                AtomicJsonFile.Write(
                    _installationStateFile,
                    data,
                    JsonStateSchemas.InstallOperationStateJson);
        }

        public string GetInstallState()
        {
            var result = AtomicJsonFile.ReadAndMigrate(
                _installationStateFile,
                JsonStateSchemas.InstallOperationStateJson);
            return result.Status switch
            {
                JsonStateReadStatus.Success => result.Value
                    ?? throw new InvalidDataException("Install state was empty."),
                JsonStateReadStatus.Missing => throw new FileNotFoundException(
                    "No install state exists.",
                    _installationStateFile),
                JsonStateReadStatus.UnsupportedVersion => throw new NotSupportedException(
                    $"Install state schema version {result.Version} is not supported."),
                _ => throw new InvalidDataException(
                    $"Install state is corrupt: {result.Error ?? "unknown error"}.")
            };
        }


        public async Task<System.IO.DriveInfo> GetDriveInfo(string path)
        {
            try
            {
                return await Task.Run(() =>
                {
                    var driveInfo = new System.IO.DriveInfo(Path.GetPathRoot(path));

                    if (!driveInfo.IsReady)
                    {
                        throw new Exception($"Drive {driveInfo.Name} is not ready");
                    }

                    return driveInfo;
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get drive info for path: {Path}", path);
                throw;
            }
        }

        public Task<byte[]?> GetCachedManifestBytes(string appName, string version)
        {
            try
            {
                var key = GetManifestCacheKey(appName, version);
                if (!_manifestIndex.TryGetValue(key, out var digest))
                    return Task.FromResult<byte[]?>(null);

                var manifestPath = ResolveAppDataPath("manifests", $"{digest}.manifest");
                if (!File.Exists(manifestPath))
                    return Task.FromResult<byte[]?>(null);

                var bytes = File.ReadAllBytes(manifestPath);
                var actualDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return Task.FromResult<byte[]?>(string.Equals(actualDigest, digest, StringComparison.Ordinal)
                    ? bytes
                    : null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get cached manifest bytes for app: {AppName}", appName);
                return Task.FromResult<byte[]?>(null);
            }
        }

        public Task CacheManifestBytes(string appName, string version, byte[] manifestBytes)
        {
            try
            {
                var digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
                var manifestPath = ResolveAppDataPath("manifests", $"{digest}.manifest");
                lock (_writeLock)
                {
                    if (!File.Exists(manifestPath))
                        AtomicFile.WriteAllBytes(manifestPath, manifestBytes);
                    _manifestIndex[GetManifestCacheKey(appName, version)] = digest;
                    AtomicJsonFile.Write(
                        _manifestIndexFile,
                        _manifestIndex.ToDictionary(pair => pair.Key, pair => pair.Value),
                        JsonStateSchemas.ManifestIndex);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to cache manifest bytes for app: {AppName}", appName);
            }

            return Task.CompletedTask;
        }

        private static string GetManifestCacheKey(string appName, string version) =>
            StorageKeyCodec.Encode($"{appName}\n{version}");

        private T? ReadState<T>(
            string path,
            JsonStateSchema<T> schema,
            bool authoritative)
        {
            var result = AtomicJsonFile.ReadAndMigrate(path, schema);
            if (result.IsSuccess)
            {
                if (result.Source == JsonStateSource.Backup)
                    _logger.Warning("Recovered {Category} state from its validated backup", schema.Category);
                return result.Value;
            }
            if (result.Status == JsonStateReadStatus.Missing)
                return default;

            _logger.Error(
                "Failed to read {Category} state: {Status} {Error}",
                schema.Category,
                result.Status,
                result.Error);
            if (authoritative)
                throw new InvalidDataException(
                    $"Authoritative {schema.Category} state is unavailable: {result.Status}.");
            return default;
        }
        private void PublishGameMetadataSnapshot() =>
            Volatile.Write(
                ref _gameMetadataSnapshot,
                _gameMetaDataDictionary.ToFrozenDictionary(StringComparer.Ordinal));

        private void PublishLocalAppStateSnapshot() =>
            Volatile.Write(
                ref _localAppStateSnapshot,
                _localAppStateDictionary.ToFrozenDictionary(StringComparer.Ordinal));


        private string ResolveAppDataPath(params string[] segments)
        {
            var pathParts = new string[segments.Length + 1];
            pathParts[0] = _appDataPath;
            Array.Copy(segments, 0, pathParts, 1, segments.Length);
            var candidate = Path.GetFullPath(Path.Combine(pathParts));
            var relative = Path.GetRelativePath(_appDataPath, candidate);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new InvalidOperationException("Application data path escaped its canonical root.");

            return candidate;
        }
    }
}
