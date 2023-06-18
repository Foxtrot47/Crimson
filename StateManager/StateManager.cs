using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    static StateManager()
    {
        // Setup logging
        var dateTime = DateTime.Now.ToString("yyyy-MM-dd");
        var logFilePath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\logs\{dateTime}.txt";
        legendaryBinaryPath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\bin\{dateTime}.txt";
        Log = new LoggerConfiguration().WriteTo.File(logFilePath).CreateLogger();

        _gameDataFile = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\storage\gamedata.json";

        // If stored data exists load it
        if (File.Exists(_gameDataFile))
        {
            var data = File.Open(_gameDataFile, FileMode.Open);
            _gameData = JsonSerializer.Deserialize<ObservableCollection<Game>>(data);
        }
        // Else create new data and save it
        else
            _gameData = new ObservableCollection<Game>();

        // Initial Library Fetch
        UpdateLibrary();

        // Create a timer with a 10-minute interval
        _timer = new Timer(600000);

        // Subscribe to the Elapsed event of the timer
        _timer.Elapsed += (sender, e) =>
        {
            // Call the UpdateJsonFile method when the timer elapses
            UpdateJsonFile();
        };

        // Start the timer when the application starts
        _timer.Start();
    }

    // Declare a method to update the JSON file when the data changes
    // Method must be public as it should be called when program is being terminated
    // ReSharper disable once MemberCanBePrivate.Global
    public static void UpdateJsonFile()
    {
        try
        {
            // Serialize the games list to a JSON string
            var jsonString = JsonSerializer.Serialize(_gameData);

            // Write the JSON string to the JSON file
            File.WriteAllText(_gameDataFile, jsonString);
        }
        catch (Exception exception)
        {
            Log.Error("UpdateJsonFile: Error while saving updating json {Exception}", exception.ToString());
        }
    }

    // <summary>
    // Handles the updated game info from legendary and compares it with current game data
    // </summary>
    public static void UpdateLibrary()
    {
        var legendaryHandle = new Legendary(legendaryBinaryPath);
        legendaryHandle.GetLibraryData().ContinueWith(t =>
        {
            var games = t.Result;
            // ReSharper disable once CommentTypo
            // User do not have any games in his library, pathetic lmao
            if (games == null)
                return;

            // Find the games that need to be added to _gameData
            var gamesToAdd = games.Except(_gameData);

            // Find the games that need to be removed from _gameData
            var gamesToRemove = _gameData.Except(games);

            // Find the games that need to be updated in _gameData
            var gamesToUpdate = games.Intersect(_gameData);

            // Add the new games to _gameData
            foreach (var game in gamesToAdd)
                _gameData.Add(game);

            // Remove the old games from _gameData
            foreach (var game in gamesToRemove)
                _gameData.Remove(game);

            // Update the existing games in _gameData
            foreach (var game in gamesToUpdate)
            {
                // Update some properties of game in _gameData
                // For example, update the title and images
                var existingGame = _gameData.First(g => g.Name == game.Name);
                existingGame.Title = game.Title;
                existingGame.Images = game.Images;
            }
        });
    }

    // Stop the timer when the application exits
    public static void Dispose()
    {
        _timer.Stop();
    }
}