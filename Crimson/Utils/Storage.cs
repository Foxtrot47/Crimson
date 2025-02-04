﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;

namespace Crimson.Utils
{
    public class Storage
    {
        private static readonly string AppDataPath = $@"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\Crimson";
        private static readonly string UserDataFile;
        private static readonly string GameAssetsFile;
        private static readonly string MetaDataDirectory;
        private static readonly string SettingsDataFile;
        private static readonly string InstallationStateFile;
        private static readonly string InstalledGamesFile;

        private Dictionary<string, Game> _gameMetaDataDictionary;
        private Dictionary<string, InstalledGame> _installedGamesDictionary;
        private ILogger _logger;

        public Dictionary<string, Game> GameMetaDataDictionary => _gameMetaDataDictionary;
        public Dictionary<string, InstalledGame> InstalledGamesDictionary => _installedGamesDictionary;

        public string DefaultInstallPath => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        static Storage()
        {
            UserDataFile = $@"{AppDataPath}\user.json";
            GameAssetsFile = $@"{AppDataPath}\assets.json";
            MetaDataDirectory = $@"{AppDataPath}\metadata";
            SettingsDataFile = $@"{AppDataPath}\settings.json";
            InstallationStateFile = $@"{AppDataPath}\install_state.json";
            InstalledGamesFile = $@"{AppDataPath}\installed.json";
        }

        public Storage()
        {
            _logger = App.GetService<ILogger>();
            try
            {
                if (!Directory.Exists(MetaDataDirectory))
                    Directory.CreateDirectory(MetaDataDirectory);

                var metaDataDictionary = new Dictionary<string, Game>();

                Parallel.ForEach(Directory.EnumerateFiles(MetaDataDirectory),
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (file) =>
                    {
                        try
                        {
                            var fileName = Path.GetFileName(file);
                            var gameName = fileName[..^5];
                            var jsonString = File.ReadAllText(file);

                            var gameMetaData = JsonSerializer.Deserialize<Game>(jsonString);

                            // Use lock to ensure thread safety when modifying the dictionary
                            lock (metaDataDictionary)
                            {
                                metaDataDictionary.Add(gameName, gameMetaData);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log detailed exception information
                            Log.Error($"Error processing file {file}. Exception: {ex}");
                        }
                    });

                // Outside the parallel loop, assign the dictionary to the shared field
                _gameMetaDataDictionary = metaDataDictionary;

                // Load installed games list
                if (!File.Exists(InstalledGamesFile))
                {
                    _installedGamesDictionary = new Dictionary<string, InstalledGame>();
                }
                else
                {
                    var jsonString = File.ReadAllText(InstalledGamesFile);
                    if (jsonString != null && jsonString != "")
                        _installedGamesDictionary =
                            JsonSerializer.Deserialize<Dictionary<string, InstalledGame>>(jsonString);
                    else
                        _installedGamesDictionary = new Dictionary<string, InstalledGame>();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
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
                    File.Open(GameAssetsFile, FileMode.Create, FileAccess.Write, FileShare.Read);
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
            return _gameMetaDataDictionary.TryGetValue(gameName, out var gameMetaData) ? gameMetaData : null;
        }

        public void SaveMetaData(Game game)
        {
            var jsonString = JsonSerializer.Serialize(game);

            if (!Directory.Exists(MetaDataDirectory))
                Directory.CreateDirectory(MetaDataDirectory);

            var fileName = $@"{MetaDataDirectory}\{game.AppName}.json";
            File.WriteAllText(fileName, jsonString);

            _gameMetaDataDictionary.TryAdd(game.AppName, game);
        }

        public void UpdateInstalledGames(Dictionary<string, InstalledGame> installedGamesDict)
        {
            _installedGamesDictionary = installedGamesDict;

            var jsonString = JsonSerializer.Serialize(_installedGamesDictionary);

            var fileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\installed.json";
            File.WriteAllText(fileName, jsonString);
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
            await File.WriteAllBytesAsync(Path.Join(AppDataPath, $"{appName}.manifest"), manifestBytes);
        }

        public static async Task<byte[]> GetAppManifest(string appName)
        {
            var data = await File.ReadAllBytesAsync(Path.Join(AppDataPath, $"{appName}.manifest"));
            return data;
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
    }
}
