using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Serilog;
using Windows.Storage;

namespace Crimson.Core;

public static class StateManager
{
    // File contains game data
    private static string _gameDataFile;
    // ObservableCollection to store the game info objects and subscribe to its events
    private static ObservableCollection<Game> _gameData;
    // Timer to trigger the file update periodically
    private static Timer _timer;
    private static ILogger _log;
    private static string _legendaryBinaryPath;

    public static event Action<ObservableCollection<Game>> LibraryUpdated;
    public static event Action<Game> GameStatusUpdated;

    public static void Initialize(string binaryPath, ILogger log)
    {
        _legendaryBinaryPath = binaryPath;
        _log = log;


        var localFolder = ApplicationData.Current.LocalFolder;
        _gameDataFile = $@"{localFolder.Path}\gamedata.json";

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

            await using var fileStream = File.Open(_gameDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(jsonString);
            await streamWriter.FlushAsync();
        }
        catch (Exception exception)
        {
            _log.Error("UpdateJsonFile: Error while updating json {Exception}", exception.ToString());
        }
    }

    // <summary>
    // Handles the updated game info from legendary and compares it with current game data
    // </summary>
    public static async Task UpdateLibraryAsync()
    {
        try
        {
            var legendaryHandle = new Legendary(_legendaryBinaryPath, _log);
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
                    _log.Information("UpdateLibraryAsync: Checking {Game}", existingGame.Name);

                    // Possible sign that legendary might got have killed and is reporting wrong game state
                    if (existingGame.State == Game.InstallState.Installing &&
                        installedGame.State == Game.InstallState.Installed)
                    {
                        existingGame.State = Game.InstallState.Installing;
                        _log.Information("UpdateLibraryAsync: Game {Game} is still installing", existingGame.Name);
                    }

                    // Legendary detected new version and we need to update game
                    else if (existingGame.Version != null && installedGame.Version != existingGame.Version)
                    {
                        existingGame.State = Game.InstallState.NeedUpdate;
                        _log.Information("UpdateLibraryAsync: Game {Game} needs update", existingGame.Name);
                    }

                    // Don't change games state if we have update pending
                    else if (existingGame.State != Game.InstallState.NeedUpdate)
                    {
                        existingGame.State = installedGame.State;
                        _log.Information("UpdateLibraryAsync: Game {Game} state changed to {State}", existingGame.Name, existingGame.State);
                    }

                    existingGame.Version = installedGame.Version;
                    existingGame.InstallLocation = installedGame.InstallLocation;
                }
            }
            _log.Information("UpdateLibraryAsync: Library updated");
            LibraryUpdated?.Invoke(_gameData);
            await UpdateJsonFileAsync();
        }
        catch (Exception ex)
        {
            _log.Error("UpdateLibraryAsync: Error updating library {Exception}", ex.Message);
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

        GameStatusUpdated?.Invoke(game);
        InstallManager.AddToQueue(new InstallItem(gameName, actionType, location));
        _log.Information("AddToInstallationQueue: {Game} {Action} {location}", gameName, actionType, location);
    }

    public static void FinishedInstall(InstallItem item)
    {
        var game = _gameData.FirstOrDefault(game => game.Name == item.AppName);
        if (game == null) return;

        _log.Information("FinishedInstall: {Game} {Action} {Status}", item.AppName, item.Action, item.Status);
        if (item.Action == ActionType.Uninstall && item.Status == ActionStatus.Success)
            game.State = Game.InstallState.NotInstalled;

        else if (item.Action is ActionType.Install or ActionType.Update or ActionType.Repair && item.Status == ActionStatus.Success)
            game.State = Game.InstallState.Installed;

        else if (item.Action == ActionType.Install && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.NotInstalled;

        else if (item.Action == ActionType.Update && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.NeedUpdate;

        else if (item.Action == ActionType.Repair && item.Status is ActionStatus.Failed or ActionStatus.Cancelled)
            game.State = Game.InstallState.Broken;

        GameStatusUpdated?.Invoke(game);
    }
    public static Game GetGameData(string gameName)
    {
        var legendaryHandle = new Legendary(_legendaryBinaryPath, _log);
        var data = legendaryHandle.GetGameData(gameName);
        var game = _gameData.FirstOrDefault(game => game.Name == gameName);
        game.DownloadSizeMiB = data.DownloadSizeMiB;
        game.DiskSizeMiB = data.DiskSizeMiB;
        _log.Information("GetGameData: {Game} {DownloadSizeMiB} {DiskSizeMiB}", gameName, data.DownloadSizeMiB, data.DiskSizeMiB);
        ;
        return game;
    }

    // Stop the timer when the application exits
    public static void Dispose()
    {
        _timer.Stop();
    }
    public static void StartGame(string name)
    {
        var game = _gameData.FirstOrDefault(game => game.Name == name);
        if (game == null) return;

        var legendaryHandle = new Legendary(_legendaryBinaryPath, _log);
        legendaryHandle.StartGame(game.Name);
    }
}