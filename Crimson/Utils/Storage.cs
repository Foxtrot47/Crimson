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
        private static readonly string AppDataPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Crimson"));
        private static readonly string UserDataFile = ResolveAppDataPath("user.json");
        private static readonly string GameAssetsFile = ResolveAppDataPath("assets.json");
        private static readonly string MetaDataDirectory = ResolveAppDataPath("metadata");
        private static readonly string SettingsDataFile = ResolveAppDataPath("settings.json");
        private static readonly string InstallationStateFile = ResolveAppDataPath("install_state.json");
        private static readonly string LocalAppStateFile = ResolveAppDataPath("localstate.json");
        private static readonly string ManifestPath = ResolveAppDataPath("manifests");

        private Dictionary<string, Game> _gameMetaDataDictionary = new();
        private Dictionary<string, LocalAppState> _localAppStateDictionary = new();
        private ILogger _logger;

        public Dictionary<string, Game> GameMetaDataDictionary => _gameMetaDataDictionary;
        public Dictionary<string, LocalAppState> LocalAppStateDictionary => _localAppStateDictionary;

        public string DefaultInstallPath => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);


        public Storage()
        {
            _logger = App.GetService<ILogger>();
            try
            {
                if (!Directory.Exists(MetaDataDirectory))
                    Directory.CreateDirectory(MetaDataDirectory);

                if (!Directory.Exists(ManifestPath))
                    Directory.CreateDirectory(ManifestPath);

                var metaDataDictionary = new Dictionary<string, Game>();

                Parallel.ForEach(Directory.EnumerateFiles(MetaDataDirectory, "*.json"),
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (file) =>
                    {
                        try
                        {
                            var jsonString = File.ReadAllText(file);
                            var gameMetaData = JsonSerializer.Deserialize<Game>(jsonString);
                            if (gameMetaData == null)
                                throw new InvalidDataException("Metadata file did not contain a game record.");

                            lock (metaDataDictionary)
                            {
                                metaDataDictionary.Add(gameMetaData.AppName, gameMetaData);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error processing metadata file {File}", file);
                        }
                    });

                _gameMetaDataDictionary = metaDataDictionary;

                // Load installed games list
                if (!File.Exists(LocalAppStateFile))
                {
                    _localAppStateDictionary = new Dictionary<string, LocalAppState>();
                }
                else
                {
                    var jsonString = File.ReadAllText(LocalAppStateFile);
                    if (jsonString != null && jsonString != "")
                        _localAppStateDictionary =
                            JsonSerializer.Deserialize<Dictionary<string, LocalAppState>>(jsonString)
                            ?? new Dictionary<string, LocalAppState>();
                    else
                        _localAppStateDictionary = new Dictionary<string, LocalAppState>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize storage");
            }
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
            var path = ResolveAppDataPath($"{appName}.manifest");
            await File.WriteAllBytesAsync(path, manifestBytes);
        }

        public static Task<byte[]> GetAppManifest(string appName)
        {
            var path = ResolveAppDataPath($"{appName}.manifest");
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

        private static string GetManifestCachePath(string appName, string version) => ResolveAppDataPath(
            "manifests",
            $"{appName}_{version}.manifest");

        private static string ResolveAppDataPath(params string[] segments)
        {
            var pathParts = new string[segments.Length + 1];
            pathParts[0] = AppDataPath;
            Array.Copy(segments, 0, pathParts, 1, segments.Length);
            var candidate = Path.GetFullPath(Path.Combine(pathParts));
            var relative = Path.GetRelativePath(AppDataPath, candidate);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new InvalidOperationException("Application data path escaped its canonical root.");

            return candidate;
        }
    }
}
