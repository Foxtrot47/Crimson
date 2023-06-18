using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUiApp.StateManager;

public class Legendary
{
    private string _legendaryBinaryPath;

    public Legendary(string legendaryBinaryPath)
    {
        _legendaryBinaryPath = legendaryBinaryPath;
    }
    
    public Task<ObservableCollection<Game>> GetLibraryData()
    {
        // Create a new task to run the function logic
        var task = new Task<ObservableCollection<Game>>(() =>
        {
            ObservableCollection<Game> gameList = null;
            var process = new Process();
            process.StartInfo.FileName = _legendaryBinaryPath;
            // Output installed games as JSON
            process.StartInfo.Arguments = "list --json";
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
                var game = new Game();
                game.Name = item.GetProperty("app_name").GetString();
                game.Title = item.GetProperty("app_title").GetString();

                // Get the keyImages
                var keyImages = item
                    .GetProperty("metadata")
                    .GetProperty("keyImages")
                    .EnumerateArray();

                var image = new Game.Image();
                foreach (var keyImage in keyImages)
                {
                    image.Width = keyImage.GetProperty("width").GetInt32();
                    image.Height = keyImage.GetProperty("height").GetInt32();
                
                    // we are taking image with resolution 1200 x 1600 for proper cropping
                    if (keyImage.GetProperty("type").GetString() == "DieselGameBoxTall") 
                        // Pass height and width to url to get cropped image
                        image.Url = keyImage.GetProperty("url").GetString() + "?h=400&resize=1&w=300";
                
                    // For other images, don't crop
                    else image.Url = keyImage.GetProperty("url").GetString();
                
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
}
