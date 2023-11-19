using Crimson.Core;
using Crimson.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Crimson.Utils
{
    internal static class Storage
    {
        private static readonly string UserDataFile = $@"{ApplicationData.Current.LocalFolder.Path}\user.json";
        private static readonly string GameAssetsFile = $@"{ApplicationData.Current.LocalFolder.Path}\assets.json";

        private static readonly Dictionary<string, GameMetaData> _gameMetaDataDictionary;

        public static Dictionary<string, GameMetaData> GameMetaDataDictionary => _gameMetaDataDictionary;

        static Storage()
        {
            try
            {
                var metadataDirectory = $@"{ApplicationData.Current.LocalFolder.Path}\metadata";
                if (!Directory.Exists(metadataDirectory))
                    Directory.CreateDirectory(metadataDirectory);

                var metaDataDictionary = new Dictionary<string, GameMetaData>();

                Parallel.ForEach(Directory.EnumerateFiles(metadataDirectory), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (file) =>
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var gameName = fileName[..^5];
                        var jsonString = File.ReadAllText(file);

                        var gameMetaData = JsonSerializer.Deserialize<GameMetaData>(jsonString);

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
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }

        }

        public static async Task<UserData> GetUserData()
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
        public static async Task SaveUserData(UserData data)
        {
            var jsonString = JsonSerializer.Serialize(data);

            await using var fileStream = File.Open(UserDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(jsonString);
            streamWriter.Close();
        }

        public static async Task<IEnumerable<Asset>> GetGameAssetsData()
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
        public static async Task SaveGameAssetsData(IEnumerable<Asset> data)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(data);

                await using var fileStream = File.Open(GameAssetsFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                await using var streamWriter = new StreamWriter(fileStream);
                await streamWriter.WriteAsync(jsonString);
                await streamWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        public static GameMetaData GetGameMetaData(string gameName)
        {
            return _gameMetaDataDictionary.TryGetValue(gameName, out var gameMetaData) ? gameMetaData : null;
        }
        public static void SaveMetaData(GameMetaData gameMetaData)
        {
            var jsonString = JsonSerializer.Serialize(gameMetaData);

            var metadataDirectory = $@"{ApplicationData.Current.LocalFolder.Path}\metadata";
            if (!Directory.Exists(metadataDirectory))
                Directory.CreateDirectory(metadataDirectory);

            var fileName = $@"{metadataDirectory}\{gameMetaData.AppName}.json";
            File.WriteAllText(fileName, jsonString);

            _gameMetaDataDictionary.TryAdd(gameMetaData.AppName, gameMetaData);
        }

    }
}
