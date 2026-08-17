using System;
using System.IO;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.Core;

public class SettingsManager
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsManager> _logger;

    private Settings Settings { get; set; }

    public SettingsManager(ISettingsStore store, ILogger<SettingsManager> logger)
    {
        _store = store;
        _logger = logger;
        Settings = LoadSettings();
    }

    public bool MicEnabled { get => Settings.MicaEnabled; set { Settings.MicaEnabled = value; } }

    public string DefaultInstallLocation
    {
        get => Settings.DefaultInstallLocation ?? "C:\\Games\\";
        set { Settings.DefaultInstallLocation = value; }
    }

    public string LogsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Crimson",
        "logs");

    private Settings LoadSettings()
    {
        try
        {
            return _store.Get() ?? new Settings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LoadSettings: Exception: {ex}", ex);
            return new Settings();
        }
    }

    public Task SaveSettings()
    {
        _store.Save(Settings);
        return Task.CompletedTask;
    }

}
