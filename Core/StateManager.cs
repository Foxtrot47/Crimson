using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Serilog;

namespace WinUiApp.Core;

public static class StateManager
{
    // File contains game data
    private static string _gameDataFile;
    // ObservableCollection to store the game info objects and subscribe to its events
    private static ObservableCollection<Game> _gameData;
    // Timer to trigger the file update periodically
    private static Timer _timer;
    private static readonly ILogger Log;
    private static readonly string legendaryBinaryPath;
    private static FileStream _fileStream;

    public static event Action<ObservableCollection<Game>> LibraryUpdated;

    static StateManager()
    {
        // Setup logging
        var dateTime = DateTime.Now.ToString("yyyy-MM-dd");
        var logFilePath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\logs\{dateTime}.txt";
        legendaryBinaryPath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\bin\legendary.exe";
        Log = new LoggerConfiguration().WriteTo.File(logFilePath).CreateLogger();

        _gameDataFile = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\storage\gamedata.json";

        // If stored data exists load it
        if (File.Exists(_gameDataFile))
        {
            try
            {
                using var data = File.Open(_gameDataFile, FileMode.Open);
                _gameData = JsonSerializer.Deserialize<ObservableCollection<Game>>(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
        // Else create new data and save it
        else
            _gameData = new ObservableCollection<Game>();

        // Create a timer with a 10-minute interval
        _timer = new Timer(600000);

        // Subscribe to the Elapsed event of the timer
        _timer.Elapsed += (sender, e) =>
        {
            // Call the UpdateJsonFile method when the timer elapses
            UpdateJsonFileAsync();
        };

        // Start the timer when the application starts
        _timer.Start();
    }

    // Declare a method to update the JSON file when the data changes
    // Method must be public as it should be called when program is being terminated
    // ReSharper disable once MemberCanBePrivate.Global
    public static async Task UpdateJsonFileAsync()
    {
        try
        {
            // Serialize the games list to a JSON string
            var jsonString = JsonSerializer.Serialize(_gameData);

            using (var fileStream = File.Open(_gameDataFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await using var streamWriter = new StreamWriter(fileStream);
                await streamWriter.WriteAsync(jsonString);
                await streamWriter.FlushAsync();
            }
        }
        catch (Exception exception)
        {
            Log.Error("UpdateJsonFile: Error while updating json {Exception}", exception.ToString());
        }
    }

    // <summary>
    // Handles the updated game info from legendary and compares it with current game data
    // </summary>
    public static async Task UpdateLibraryAsync()
    {
        try
        {
            var legendaryHandle = new Legendary(legendaryBinaryPath);
            var legendaryGameList = await legendaryHandle.GetLibraryData();

            // ReSharper disable once CommentTypo
            // User do not have any games in his library, pathetic lmao
            if (legendaryGameList == null)
                return;

            _gameData ??= new ObservableCollection<Game>();

            // Find the games that need to be removed from _gameData
            var gamesToRemove = _gameData.Where(existingGame => legendaryGameList.All(game => game.Name != existingGame.Name))
                .ToList();

            foreach (var game in gamesToRemove)
                _gameData.Remove(game);


            // Disable for now. Its causing unwanted bugs
            //// Find the games that need to be updated in _gameData
            //// We only need to consider change in game title or image changes
            //var gamesToUpdate = _gameData.Where(existingGame => legendaryGameList.Any(game => game.Name == existingGame.Name)).ToList();

            //foreach (var game in gamesToUpdate)
            //{
            //    var existingGame = _gameData.FirstOrDefault(g => g.Name == game.Name);
            //    if (existingGame == null)
            //        continue;

            //    // Do not set title to null for whatever reason
            //    if (existingGame.Title != null) existingGame.Title = game.Title;

            //    // same thing
            //    if (game.Images == null) continue;

            //    existingGame.Images.Clear();
            //    foreach (var image in game.Images)
            //        existingGame.Images.Add(image);
            //}

            // Find the games that need to be added to _gameData
            // Doing this at last because we don't want to add and update the same games
            var gamesToAdd = legendaryGameList.Where(game => _gameData.All(existingGame => existingGame.Name != game.Name))
                .ToList();

            foreach (var game in gamesToAdd)
                _gameData.Add(game);

            // TODO: Improve this dumb logic
            var installedGamesList = await legendaryHandle.GetInstalledGames();
            if (installedGamesList != null)
            {
                foreach (var installedGame in installedGamesList)
                {
                    var existingGame = _gameData.FirstOrDefault(g => g.Name == installedGame.Name);
                    if (existingGame == null)
                        continue;

                    // Possible sign that legendary might got have killed and is reporting wrong game state
                    if (existingGame.State == Game.InstallState.Installing && installedGame.State == Game.InstallState.Installed)
                        existingGame.State = Game.InstallState.Installing;

                    // Legendary detected new version and we need to update game
                    else if (existingGame.Version != null && installedGame.Version != existingGame.Version)
                        existingGame.State = Game.InstallState.NeedUpdate;

                    // Don't change games state if we have update pending
                    else if(existingGame.State != Game.InstallState.NeedUpdate)
                        existingGame.State = installedGame.State;

                    existingGame.Version = installedGame.Version;
                    existingGame.InstallLocation = installedGame.InstallLocation;
                }
            }

            LibraryUpdated?.Invoke(_gameData);
            await UpdateJsonFileAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UpdateLibraryAsync: Error updating library {Exception}", ex.Message);
        }
    }

    public static ObservableCollection<Game> GetLibraryData()
    {
        return _gameData;
    }

    public static Game GetGameInfo(string name)
    {
        return _gameData.FirstOrDefault(game => game.Name == name);
    }

    public static void AddToInstallationQueue(string gameName, ActionType actionType, string location)
    {
        var game = _gameData.FirstOrDefault(g => g.Name == gameName);
        if (game == null) return;

        if (actionType == ActionType.Install) 
            game.State = Game.InstallState.Installing;

        InstallManager.AddToQueue(new InstallItem(gameName, actionType, location));
    }

    // Stop the timer when the application exits
    public static void Dispose()
    {
        _timer.Stop();
    }
}