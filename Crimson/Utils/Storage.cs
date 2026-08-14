using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Utils
{
    public class Storage
    {
        private readonly string _appDataPath;
        private readonly string _userDataFile;
        private readonly string _gameAssetsFile;
        private readonly string _metaDataDirectory;
        private readonly string _settingsDataFile;
        private readonly string _installationStateFile;
        private readonly string _localAppStateFile;
        private readonly string _manifestPath;
        private Dictionary<string, Game> _gameMetaDataDictionary;
        private Dictionary<string, LocalAppState> _localAppStateDictionary;
        private readonly ILogger _logger;

        public Dictionary<string, Game> GameMetaDataDictionary => _gameMetaDataDictionary;
        public Dictionary<string, LocalAppState> LocalAppStateDictionary => _localAppStateDictionary;

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
            _settingsDataFile = ResolveAppDataPath("settings.json");
            _installationStateFile = ResolveAppDataPath("install_state.json");
            _localAppStateFile = ResolveAppDataPath("localstate.json");
            _manifestPath = ResolveAppDataPath("manifests");

            Directory.CreateDirectory(_metaDataDirectory);
            Directory.CreateDirectory(_manifestPath);
            _gameMetaDataDictionary = new Dictionary<string, Game>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_metaDataDirectory, "*.json"))
            {
                try
                {
                    var game = JsonSerializer.Deserialize<Game>(File.ReadAllText(file));
                    if (game == null)
                        throw new InvalidDataException("Metadata file did not contain a game record.");

                    _ = StorageKeyCodec.Encode(game.AppName);
                    _gameMetaDataDictionary.Add(game.AppName, game);
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        "Failed to load metadata file {FileName} with {ErrorType}",
                        Path.GetFileName(file),
                        exception.GetType().Name);
                }
            }

            if (!File.Exists(_localAppStateFile))
            {
                _localAppStateDictionary = new Dictionary<string, LocalAppState>(StringComparer.Ordinal);
            }
            else
            {
                var json = File.ReadAllText(_localAppStateFile);
                _localAppStateDictionary = string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, LocalAppState>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, LocalAppState>>(json)
                      ?? new Dictionary<string, LocalAppState>(StringComparer.Ordinal);
            }
        }

        public async Task<UserData> GetUserData()
        {
            if (!File.Exists(_userDataFile))
            {
                await SaveUserData(null);
                return null;
            }

            await using var fileStream = File.Open(_userDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var jsonString = await streamReader.ReadToEndAsync();
            var userData = JsonSerializer.Deserialize<UserData>(jsonString);
            streamReader.Dispose();

            return userData;
        }

        public async Task SaveUserData(UserData data)
        {
            var jsonString = JsonSerializer.Serialize(data);

            await using var fileStream = File.Open(_userDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(jsonString);
            streamWriter.Close();
        }

        public async Task ClearUserData()
        {
            try
            {
                if (File.Exists(_userDataFile))
                    File.Delete(_userDataFile);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to clear user data file");
            }
        }

        public async Task<IEnumerable<Asset>> GetGameAssetsData()
        {
            try
            {
                if (!File.Exists(_gameAssetsFile))
                {
                    await SaveGameAssetsData(null);
                    return null;
                }

                await using var fileStream = File.Open(_gameAssetsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var streamReader = new StreamReader(fileStream);
                var jsonString = await streamReader.ReadToEndAsync();
                streamReader.Close();
                return JsonSerializer.Deserialize<IEnumerable<Asset>>(jsonString);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
                return null;
            }
        }

        public async Task SaveGameAssetsData(IEnumerable<Asset> data)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(data);

                await using var fileStream =
                    File.Open(_gameAssetsFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                await using var streamWriter = new StreamWriter(fileStream);
                await streamWriter.WriteAsync(jsonString);
                await streamWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
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
            var jsonString = JsonSerializer.Serialize(game);

            if (!Directory.Exists(_metaDataDirectory))
                Directory.CreateDirectory(_metaDataDirectory);

            var fileName = ResolveAppDataPath("metadata", $"{StorageKeyCodec.Encode(game.AppName)}.json");
            File.WriteAllText(fileName, jsonString);

            // Overwrite existing entry so in-memory state stays current
            _gameMetaDataDictionary[game.AppName] = game;
        }

        public void UpdateLocalAppState(Dictionary<string, LocalAppState> installedGamesDict)
        {
            _localAppStateDictionary = installedGamesDict;

            var jsonString = JsonSerializer.Serialize(_localAppStateDictionary);

            File.WriteAllText(_localAppStateFile, jsonString);
        }

        public void AddToLocalAppState(string appName, LocalAppState appState)
        {
            _localAppStateDictionary[appName] = appState;

            var jsonString = JsonSerializer.Serialize(_localAppStateDictionary);

            File.WriteAllText(_localAppStateFile, jsonString);
        }

        public string GetSettingsData()
        {
            using var fileStream = File.Open(_settingsDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var data = streamReader.ReadToEnd();
            fileStream.Dispose();
            return data;
        }

        public async Task SaveSettingsData(string data)
        {
            await using var fileStream = File.Open(_settingsDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(data);
            streamWriter.Close();
        }

        public void SaveInstallState(string data)
        {
            using var fileStream = File.Open(_installationStateFile, FileMode.Create, FileAccess.Write, FileShare.None);
            using var streamWriter = new StreamWriter(fileStream);
            streamWriter.Write(data);
            streamWriter.Close();
        }

        public string GetInstallState()
        {
            using var fileStream = File.Open(_installationStateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var data = streamReader.ReadToEnd();
            fileStream.Close();
            return data;

        }

        public async Task SaveAppManifest(byte[] manifestBytes, string appName)
        {
            var path = ResolveAppDataPath($"{StorageKeyCodec.Encode(appName)}.manifest");
            await File.WriteAllBytesAsync(path, manifestBytes);
        }

        public Task<byte[]> GetAppManifest(string appName)
        {
            var path = ResolveAppDataPath($"{StorageKeyCodec.Encode(appName)}.manifest");
            return File.ReadAllBytesAsync(path);
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

        public async Task<byte[]> GetCachedManifestBytes(string appName, string version)
        {
            try
            {
                var manifestPath = GetManifestCachePath(appName, version);

                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                return await File.ReadAllBytesAsync(manifestPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get cached manifest bytes for app: {AppName}", appName);
                return null;
            }

        }

        public async Task CacheManifestBytes(string appName, string version, byte[] manifestBytes)
        {
            try
            {
                var manifestPath = GetManifestCachePath(appName, version);
                await File.WriteAllBytesAsync(manifestPath, manifestBytes);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to cache manifest bytes for app: {AppName}", appName);
            }
        }

        private string GetManifestCachePath(string appName, string version) => ResolveAppDataPath(
            "manifests",
            $"{StorageKeyCodec.Encode(appName)}.{StorageKeyCodec.Encode(version)}.manifest");

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
