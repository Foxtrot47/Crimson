using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinUiApp.Core;

public class Legendary
{
    private string _legendaryBinaryPath;
    public event Action<AuthenticationStatus> AuthenticationStatusChanged;
    public Legendary(string legendaryBinaryPath)
    {
        _legendaryBinaryPath = legendaryBinaryPath;
    }

    public Task<ObservableCollection<Game>> GetGameData(string name)
    {
        // Create a new task to run the function logic
        var task = new Task<ObservableCollection<Game>>(() =>
        {
            var gameList = new ObservableCollection<Game>();
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

            if (info.GetProperty("install").GetProperty("install_path").GetString() != null)
            {
                game.InstallLocation = info.GetProperty("install").GetProperty("install_path").GetString();
                game.Version = info.GetProperty("install").GetProperty("version").GetString();
                game.State = Game.InstallState.Installed;
            }
            gameList.Add(game);
            return gameList;
        });

        // Start the task and return it
        task.Start();
        return task;
    }

    public Task<ObservableCollection<Game>> GetLibraryData()
    {
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
            var process = new Process();
            process.StartInfo.FileName = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\bin\legendary.exe";
            process.StartInfo.Arguments = "auth";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.CreateNoWindow = true;

            process.ErrorDataReceived += (_, e) => UpdateAuthStatus(e.Data);
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public void UpdateAuthStatus(string updateString)
    {
        if (updateString == null) return;

        var loginRegex = new Regex(@"\[cli\] INFO: Successfully logged in as ""(.+?)"" via WebView");
        if (updateString == "[cli] INFO: Stored credentials are still valid, if you wish to switch to a different account, run \"legendary auth --delete\" and try again.")
        {
            AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoggedIn);
        }
        else if (updateString == "[cli] INFO: Testing existing login data if present...")
        {
            AuthenticationStatusChanged.Invoke(AuthenticationStatus.Checking);
        }
        else if (updateString == "[WebViewHelper] INFO: Opening Epic Games login window...")
        {
            AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoginWindowOpen);
        }
        else if (loginRegex.Match(updateString).Success)
        {
            AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoggedIn);
        }
        else if (updateString =="[cli] ERROR: WebView login attempt failed, please see log for details.")
        {
            AuthenticationStatusChanged.Invoke(AuthenticationStatus.LoginFailed);
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
