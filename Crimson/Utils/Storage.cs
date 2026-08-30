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
        private readonly string AppDataPath;
        private readonly string UserDataFile;
        private readonly string GameAssetsFile;
        private readonly string MetaDataDirectory;
        private readonly string SettingsDataFile;
        private readonly string InstallationStateFile;
        private readonly string LocalAppStateFile;
        private readonly string ManifestPath;

        private Dictionary<string, Game> _gameMetaDataDictionary = new();
        private Dictionary<string, LocalAppState> _localAppStateDictionary = new();
        private ILogger _logger;

        public Dictionary<string, Game> GameMetaDataDictionary => _gameMetaDataDictionary;
        public Dictionary<string, LocalAppState> LocalAppStateDictionary => _localAppStateDictionary;

        public string DefaultInstallPath => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        public Storage()
            : this(Log.Logger, GetDefaultAppDataPath())
        {
        }

        public Storage(ILogger logger, string appDataPath)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrWhiteSpace(appDataPath);

            _logger = logger;
            AppDataPath = Path.GetFullPath(appDataPath);
            UserDataFile = ResolveAppDataPath("user.json");
            GameAssetsFile = ResolveAppDataPath("assets.json");
            MetaDataDirectory = ResolveAppDataPath("metadata");
            SettingsDataFile = ResolveAppDataPath("settings.json");
            InstallationStateFile = ResolveAppDataPath("install_state.json");
            LocalAppStateFile = ResolveAppDataPath("localstate.json");
            ManifestPath = ResolveAppDataPath("manifests");

            InitializeStorage();
        }

        private void InitializeStorage()
        {
            try
            {
                Directory.CreateDirectory(MetaDataDirectory);
                Directory.CreateDirectory(ManifestPath);
                _gameMetaDataDictionary = LoadMetadata();
                _localAppStateDictionary = LoadLocalAppStates();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize storage");
            }
        }

        private Dictionary<string, Game> LoadMetadata()
        {
            var metadata = new Dictionary<string, Game>();
            Parallel.ForEach(
                Directory.EnumerateFiles(MetaDataDirectory, "*.json"),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                file =>
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var game = JsonSerializer.Deserialize<Game>(json);
                        if (game is null)
                            throw new InvalidDataException("Metadata file did not contain a game record.");

                        lock (metadata)
                        {
                            metadata.Add(game.AppName, game);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error processing metadata file {File}", file);
                    }
                });
            return metadata;
        }

        private Dictionary<string, LocalAppState> LoadLocalAppStates()
        {
            if (!File.Exists(LocalAppStateFile))
                return [];

            var json = File.ReadAllText(LocalAppStateFile);
            if (string.IsNullOrEmpty(json))
                return [];

            return JsonSerializer.Deserialize<Dictionary<string, LocalAppState>>(json) ?? [];
        }

        public async Task<UserData> GetUserData()
        {
            if (!File.Exists(UserDataFile))
            {
                await SaveUserData(null);
                return null;
            }

            await using var fileStream = File.Open(UserDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var jsonString = await streamReader.ReadToEndAsync();
            var userData = JsonSerializer.Deserialize<UserData>(jsonString);
            streamReader.Dispose();

            return userData;
        }

        public async Task SaveUserData(UserData data)
        {
            var jsonString = JsonSerializer.Serialize(data);

            await using var fileStream = File.Open(UserDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(jsonString);
            streamWriter.Close();
        }

        public Task ClearUserData()
        {
            if (File.Exists(UserDataFile))
                File.Delete(UserDataFile);

            if (File.Exists(GameAssetsFile))
                File.Delete(GameAssetsFile);

            if (Directory.Exists(MetaDataDirectory))
                Directory.Delete(MetaDataDirectory, true);
            Directory.CreateDirectory(MetaDataDirectory);

            _gameMetaDataDictionary.Clear();
            return Task.CompletedTask;
        }

        public async Task<IEnumerable<Asset>> GetGameAssetsData()
        {
            try
            {
                if (!File.Exists(GameAssetsFile))
                {
                    await SaveGameAssetsData(null);
                    return null;
                }

                await using var fileStream = File.Open(GameAssetsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var streamReader = new StreamReader(fileStream);
                var jsonString = await streamReader.ReadToEndAsync();
                streamReader.Close();
                return JsonSerializer.Deserialize<IEnumerable<Asset>>(jsonString);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load game assets");
                return null;
            }
        }

        public async Task SaveGameAssetsData(IEnumerable<Asset> data)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(data);

                await using var fileStream =
                    File.Open(GameAssetsFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                await using var streamWriter = new StreamWriter(fileStream);
                await streamWriter.WriteAsync(jsonString);
                await streamWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save game assets");
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

            if (!Directory.Exists(MetaDataDirectory))
                Directory.CreateDirectory(MetaDataDirectory);

            var fileName = ResolveAppDataPath("metadata", $"{game.AppName}.json");
            File.WriteAllText(fileName, jsonString);

            // Overwrite existing entry so in-memory state stays current
            _gameMetaDataDictionary[game.AppName] = game;
        }

        public void UpdateLocalAppState(Dictionary<string, LocalAppState> installedGamesDict)
        {
            _localAppStateDictionary = installedGamesDict;

            var jsonString = JsonSerializer.Serialize(_localAppStateDictionary);

            File.WriteAllText(LocalAppStateFile, jsonString);
        }

        public void AddToLocalAppState(string appName, LocalAppState appState)
        {
            _localAppStateDictionary[appName] = appState;

            var jsonString = JsonSerializer.Serialize(_localAppStateDictionary);

            File.WriteAllText(LocalAppStateFile, jsonString);
        }

        public string GetSettingsData()
        {
            using var fileStream = File.Open(SettingsDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var data = streamReader.ReadToEnd();
            fileStream.Dispose();
            return data;
        }

        public async Task SaveSettingsData(string data)
        {
            await using var fileStream = File.Open(SettingsDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(data);
            streamWriter.Close();
        }

        public void SaveInstallState(string data)
        {
            using var fileStream = File.Open(InstallationStateFile, FileMode.Create, FileAccess.Write, FileShare.None);
            using var streamWriter = new StreamWriter(fileStream);
            streamWriter.Write(data);
            streamWriter.Close();
        }

        public string GetInstallState()
        {
            using var fileStream = File.Open(InstallationStateFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var data = streamReader.ReadToEnd();
            fileStream.Close();
            return data;

        }

        public static async Task SaveAppManifest(byte[] manifestBytes, string appName)
        {
            var path = ResolveDefaultAppDataPath($"{appName}.manifest");
            await File.WriteAllBytesAsync(path, manifestBytes);
        }

        public static Task<byte[]> GetAppManifest(string appName)
        {
            var path = ResolveDefaultAppDataPath($"{appName}.manifest");
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
            $"{appName}_{version}.manifest");

        private string ResolveAppDataPath(params string[] segments) =>
            ResolvePath(AppDataPath, segments);

        private static string ResolveDefaultAppDataPath(params string[] segments) =>
            ResolvePath(GetDefaultAppDataPath(), segments);

        private static string GetDefaultAppDataPath() => Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Crimson"));

        private static string ResolvePath(string root, params string[] segments)
        {
            var pathParts = new string[segments.Length + 1];
            pathParts[0] = root;
            Array.Copy(segments, 0, pathParts, 1, segments.Length);
            var candidate = Path.GetFullPath(Path.Combine(pathParts));
            var relative = Path.GetRelativePath(root, candidate);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new InvalidOperationException("Application data path escaped its canonical root.");

            return candidate;
        }
    }
}
