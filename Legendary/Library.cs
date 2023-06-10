using System;
using System.Diagnostics;
using System.Text.Json;

namespace WinUiApp.Legendary;

public class Library
{
    private readonly string _lgendaryBinaryLocation;

    public Library(string binaryLocation)
    {
        _lgendaryBinaryLocation = binaryLocation;
    }

    public JsonElement FetchGamesList()
    {
        try
        {
            var process = new Process();
            process.StartInfo.FileName = _lgendaryBinaryLocation;
            
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

            // parse json
            var json = JsonDocument.Parse(output).RootElement;
            process.Dispose();
            
            return json;
        }
        catch
        {
            Console.WriteLine("Failed to fetch games list");
            return default;
        }
    }
}