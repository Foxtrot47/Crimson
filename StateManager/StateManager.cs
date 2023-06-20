using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Serilog;

namespace WinUiApp.StateManager;

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

            // Find the games that need to be updated in _gameData
            // We only need to consider change in game title or image changes
            var gamesToUpdate = _gameData.Where(existingGame => legendaryGameList.Any(game => game.Name == existingGame.Name)).ToList();

            foreach (var game in gamesToUpdate)
            {
                var existingGame = _gameData.FirstOrDefault(g => g.Name == game.Name);
                if (existingGame == null)
                    continue;
                existingGame.Title = game.Title;
                existingGame.Images.Clear();
                foreach (var image in game.Images)
                    existingGame.Images.Add(image);
            }

            // Find the games that need to be added to _gameData
            // Doing this at last because we don't want to add and update the same games
            var gamesToAdd = legendaryGameList.Where(game => _gameData.All(existingGame => existingGame.Name != game.Name))
                .ToList();

            foreach (var game in gamesToAdd)
                _gameData.Add(game);

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

    // Stop the timer when the application exits
    public static void Dispose()
    {
        _timer.Stop();
    }
}