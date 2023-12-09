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
    public class Storage
    {
        private static readonly string UserDataFile;
        private static readonly string GameAssetsFile;
        private static readonly string MetaDataDirectory;

        private Dictionary<string, Game> _gameMetaDataDictionary;
        private Dictionary<string, InstalledGame> _installedGamesDictionary;
        private ILogger _logger;

        public Dictionary<string, Game> GameMetaDataDictionary => _gameMetaDataDictionary;
        public Dictionary<string, InstalledGame> InstalledGamesDictionary => _installedGamesDictionary;

        static Storage()
        {
            UserDataFile = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\user.json";
            GameAssetsFile = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\assets.json";
            MetaDataDirectory = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\metadata";
        }

        public Storage()
        {
            _logger = DependencyResolver.Resolve<ILogger>();
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
                var installedGamesFile = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\installed.json";
                if (!File.Exists(installedGamesFile))
                {
                    _installedGamesDictionary = new Dictionary<string, InstalledGame>();
                }
                else {
                    var jsonString = File.ReadAllText(installedGamesFile);
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

        public void SaveInstalledGamesList(InstalledGame installedGame)
        {
            _installedGamesDictionary.TryAdd(installedGame.AppName, installedGame);

            var jsonString = JsonSerializer.Serialize(_installedGamesDictionary);

            var fileName = $@"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\Crimson\installed.json";
            File.WriteAllText(fileName, jsonString);
        }
    }
}