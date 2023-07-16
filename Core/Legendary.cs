using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;

namespace Epsilon.Core;

public class Legendary
{
    private readonly string _legendaryBinaryPath;
    private readonly ILogger _log;
    public event Action<AuthenticationStatus> AuthenticationStatusChanged;
    public Legendary(string legendaryBinaryPath, ILogger log)
    {
        _legendaryBinaryPath = legendaryBinaryPath;
        _log = log;
    }

    public Game GetGameData(string name)
    {
        _log.Information("Getting game data for {name}", name);
        var process = new Process();
        process.StartInfo.FileName = _legendaryBinaryPath;
        // Output game info as JSON
        process.StartInfo.Arguments = $@"info {name} --json";
        // Redirect the standard output so we can read it
        process.StartInfo.RedirectStandardOutput = true;
        // Enable process output redirection
        process.StartInfo.UseShellExecute = false;
        // Set CreateNoWindow to true to hide the console window
        process.StartInfo.CreateNoWindow = true;
        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.Dispose();

        var json = JsonDocument.Parse(output).RootElement;
        var info = json.GetProperty("game");
        var game = new Game
        {
            Name = info.GetProperty("app_name").GetString(),
            DownloadSizeMiB = info.GetProperty("disk_size").GetInt64(),
            DiskSizeMiB = info.GetProperty("download_size").GetInt64()
        };
        _log.Information("Game data for {name} retrieved", name);

        if (info.GetProperty("install").GetProperty("install_path").GetString() != null)
        {
            _log.Information("Game {name} is installed", name);
            game.InstallLocation = info.GetProperty("install").GetProperty("install_path").GetString();
            game.Version = info.GetProperty("install").GetProperty("version").GetString();
            game.State = Game.InstallState.Installed;
        }
        return game;
    }

    public Task<ObservableCollection<Game>> GetLibraryData()
    {
        Log.Information("GetLibraryData: Initializing");
        // Create a new task to run the function logic
        var task = new Task<ObservableCollection<Game>>(() =>
        {
            var gameList = new ObservableCollection<Game>();
            var process = new Process();
            process.StartInfo.FileName = _legendaryBinaryPath;
            // Output installed games as JSON
            process.StartInfo.Arguments = "list --json";
            // Redirect the standard output so we can read it
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            // Set CreateNoWindow to true to hide the console window
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.Dispose();

            // parse json
            var json = JsonDocument.Parse(output).RootElement;
            foreach (var item in json.EnumerateArray())
            {
                var game = new Game();
                game.Name = item.GetProperty("app_name").GetString();
                game.Title = item.GetProperty("app_title").GetString();

                // Get the keyImages
                var keyImages = item
                    .GetProperty("metadata")
                    .GetProperty("keyImages")
                    .EnumerateArray();

                game.Images = new List<Game.Image>();
                foreach (var keyImage in keyImages)
                {
                    var image = new Game.Image(); // Create a new Game.Image object for each iteration
                    image.Width = keyImage.GetProperty("width").GetInt32();
                    image.Height = keyImage.GetProperty("height").GetInt32();
                    image.Type = keyImage.GetProperty("type").GetString();

                    // we are taking image with resolution 1200 x 1600 for proper cropping
                    if (keyImage.GetProperty("type").GetString() == "DieselGameBoxTall")
                        // Pass height and width to url to get cropped image
                        image.Url = keyImage.GetProperty("url").GetString() + "?h=400&resize=1&w=300";
                    // For other images, don't crop
                    else
                        image.Url = keyImage.GetProperty("url").GetString();

                    game.Images.Add(image);
                }
                Log.Information("GetLibraryData: Adding game {name}", game.Name);
                gameList.Add(game);
            }
            return gameList;
        });

        // Start the task and return it
        task.Start();
        return task;
    }
    public Task<ObservableCollection<Game>> GetInstalledGames()
    {
        Log.Information("GetInstalledGames: Initializing");
        // Create a new task to run the function logic
        var task = new Task<ObservableCollection<Game>>(() =>
        {
            var gameList = new ObservableCollection<Game>();
            var process = new Process();
            process.StartInfo.FileName = _legendaryBinaryPath;
            // Output installed games as JSON
            process.StartInfo.Arguments = "list-installed --json";
            // Redirect the standard output so we can read it
            process.StartInfo.RedirectStandardOutput = true;
            // Enable process output redirection
            process.StartInfo.UseShellExecute = false;
            // Set CreateNoWindow to true to hide the console window
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            process.Dispose();

            // parse json
            var json = JsonDocument.Parse(output).RootElement;
            foreach (var item in json.EnumerateArray())
            {
                var game = new Game
                {
                    Name = item.GetProperty("app_name").GetString(),
                    State = Game.InstallState.Installed,
                    InstallLocation = item.GetProperty("install_path").GetString(),
                    Version = item.GetProperty("version").GetString()
                };
                Log.Information("GetInstalledGames: Adding game {name}", game.Name);
                gameList.Add(game);
            }
            return gameList;
        });
        // Start the task and return it
        task.Start();
        return task;
    }

    public void CheckAuthentication()
    {
        try
        {
            Log.Information("CheckAuthentication: Initializing");
            var process = new Process();
            process.StartInfo.FileName = _legendaryBinaryPath;
            process.StartInfo.Arguments = "auth";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.CreateNoWindow = true;

            process.ErrorDataReceived += (_, e) => UpdateAuthStatus(e.Data);
            process.Start();
            process.BeginErrorReadLine();
            Log.Information("CheckAuthentication: Finished");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CheckAuthentication: Error");
            if (ex.StackTrace != null) Log.Error(ex.StackTrace);
        }
    }

    public void UpdateAuthStatus(string updateString)
    {
        if (updateString == null || AuthenticationStatusChanged == null) return;

        var loginRegex = new Regex(@"\[cli\] INFO: Successfully logged in as ""(.+?)"" via WebView");
        switch (updateString)
        {
            case "[cli] INFO: Stored credentials are still valid, if you wish to switch to a different account, run \"legendary auth --delete\" and try again.":
                AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoggedIn);
                Log.Information("UpdateAuthStatus: Logged in");
                break;
            case "[cli] INFO: Testing existing login data if present...":
                AuthenticationStatusChanged.Invoke(AuthenticationStatus.Checking);
                Log.Information("UpdateAuthStatus: Checking");
                break;
            case "[WebViewHelper] INFO: Opening Epic Games login window...":
                AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoginWindowOpen);
                Log.Information("UpdateAuthStatus: LoginWindowOpen");
                break;
            default:
                {
                    if (loginRegex.Match(updateString).Success)
                    {
                        AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoggedIn);
                        Log.Information("UpdateAuthStatus: Logged in");
                    }
                    else if (updateString == "[cli] ERROR: WebView login attempt failed, please see log for details.")
                    {
                        AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoginFailed);
                        Log.Warning("UpdateAuthStatus: LoginFailed");
                    }
                    else
                    {
                        Log.Information("UpdateAuthStatus: {updateString}", updateString);
                    }
                    break;
                }
        }
    }

    public static async Task<StorageFile> DownloadBinaryAsync(StorageFolder outputFolder, ILogger _log)
    {
        try
        {
            var legendaryVersion = "0.20.33";
            var binaryUrl = $"https://github.com/derrod/legendary/releases/download/{legendaryVersion}/legendary.exe";
            var binaryFile = await outputFolder.CreateFileAsync("legendary.exe", CreationCollisionOption.OpenIfExists);
            _log.Information("DownloadBinaryAsync: Checking for existing Legendary binary");

            var properties = await binaryFile.GetBasicPropertiesAsync();
            if (properties.Size > 0)
            {
                _log.Information("Legendary binary exists");
                var process = new Process();
                process.StartInfo.FileName = binaryFile.Path;
                process.StartInfo.Arguments = "--version";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                process.Dispose();

                var match = Regex.Match(output, @"version ""(.*?)""");
                if (match.Success && match.Groups.Count > 1 && match.Groups[1].Value == legendaryVersion)
                {
                    _log.Error("DownloadBinaryAsync: Legendary binary exists and matches required version");
                    return binaryFile;
                }
            }

            var httpClient = new HttpClient();
            var binaryData = await httpClient.GetByteArrayAsync(binaryUrl);

            _log.Information("DownloadBinaryAsync: Downloading Legendary binary");
            using (var stream = await binaryFile.OpenStreamForWriteAsync())
            {
                await stream.WriteAsync(binaryData);
            }
            _log.Information("DownloadBinaryAsync: Legendary binary downloaded");
            return binaryFile;
        }
        catch (Exception ex)
        {
            _log.Information("DownloadBinaryAsync: Legendary binary download failed");
            _log.Information(ex.Message);
            return null;
        }
    }

    internal void StartGame(string name)
    {
        try
        {
            Log.Information("StartGame: Starting game {name}", name);
            var process = new Process();
            process.StartInfo.FileName = _legendaryBinaryPath;
            process.StartInfo.Arguments = $"launch {name}";
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            process.WaitForExit();
            process.Dispose();
            Log.Information("StartGame: Finished");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "StartGame: Error");
            if (ex.StackTrace != null) Log.Error(ex.StackTrace);
        }
    }

    public enum AuthenticationStatus
    {
        LoggedOut,
        Checking,
        LoginWindowOpen,
        LoggingIn,
        LoggedIn,
        LoginFailed
    }
}
