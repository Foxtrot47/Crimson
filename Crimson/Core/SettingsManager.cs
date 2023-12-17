using System;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.Core;

public class SettingsManager
{
    private readonly Storage _storage;
    private readonly ILogger<SettingsManager> _logger;

    private Settings Settings { get; set; }

    public SettingsManager(Storage storage, ILogger<SettingsManager> logger)
    {
        _storage = storage;
        _logger = logger;
        Settings = LoadSettings();
    }

    public bool MicEnabled { get => Settings.MicaEnabled; set { Settings.MicaEnabled = value; } }

    public string DefaultInstallLocation
    {
        get => Settings.DefaultInstallLocation ?? "C:\\Games\\";
        set { Settings.DefaultInstallLocation = value; }
    }

    public string LogsDirectory
    {
        get => $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\Crimson\\logs";
    }

    private Settings LoadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(_storage.GetSettingsData()) ?? new Settings();

        }
        catch (Exception ex)
        {
            _logger.LogWarning("LoadSettings: Exception: {ex}", ex);
            return new Settings();
        }
    }

    private async Task SaveSettings()
    {
        await _storage.SaveSettingsData(JsonSerializer.Serialize(Settings));

    }

}
