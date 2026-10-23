using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.Core;

public class SettingsManager
{
    private const double DefaultAcrylicOpacity = 60;

    private readonly Storage _storage;
    private readonly ILogger<SettingsManager> _logger;
    private readonly string _defaultInstallLocation;
    private readonly string _logsDirectory;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private Settings Settings { get; set; }

    public SettingsManager(
        Storage storage,
        ILogger<SettingsManager> logger,
        string defaultInstallLocation,
        string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultInstallLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);

        _storage = storage;
        _logger = logger;
        _defaultInstallLocation = defaultInstallLocation;
        _logsDirectory = logsDirectory;
        Settings = LoadSettings();
        StartupLanguage = Language;
    }

    public event EventHandler? AcrylicChanged;

    public bool AcrylicEnabled
    {
        // Retain the existing JSON field so saved preferences remain compatible.
        get => Settings.MicaEnabled;
        set
        {
            if (Settings.MicaEnabled == value) return;
            Settings.MicaEnabled = value;
            AcrylicChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double AcrylicOpacity
    {
        get => Settings.AcrylicOpacity ?? DefaultAcrylicOpacity;
        set
        {
            var opacity = Math.Clamp(value, 0, 100);
            if (OpacityEquals(Settings.AcrylicOpacity ?? DefaultAcrylicOpacity, opacity)) return;
            Settings.AcrylicOpacity = opacity;
            AcrylicChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool OpacityEquals(double first, double second) => Math.Abs(first - second) < 0.01;

    public bool MicEnabled { get => AcrylicEnabled; set => AcrylicEnabled = value; }

    public string DefaultInstallLocation
    {
        get => Settings.DefaultInstallLocation ?? _defaultInstallLocation;
        set { Settings.DefaultInstallLocation = value; }
    }

    public string LogsDirectory => _logsDirectory;

    public string? StartupLanguage { get; }

    public string? Language
    {
        get => AppLanguage.NormalizePreference(Settings.Language);
        set => Settings.Language = AppLanguage.NormalizePreference(value);
    }

    private Settings LoadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(_storage.GetSettingsData()) ?? new Settings();

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoadSettings failed");
            return new Settings();
        }
    }

    public async Task SaveSettings()
    {
        await _saveGate.WaitAsync();
        try
        {
            await _storage.SaveSettingsData(JsonSerializer.Serialize(Settings));
        }
        finally
        {
            _saveGate.Release();
        }
    }

}
