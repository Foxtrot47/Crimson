using System;
using System.Threading.Tasks;
using System.Threading;
using Crimson.Models;
using Microsoft.Extensions.Logging;

namespace Crimson.Core;

public class SettingsManager : ISettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsManager> _logger;
    private readonly IApplicationDirectories _directories;

    private Settings Settings { get; set; }

    public SettingsManager(
        ISettingsStore store,
        ILogger<SettingsManager> logger,
        IApplicationDirectories directories)
    {
        _store = store;
        _logger = logger;
        _directories = directories;
        Settings = LoadSettings();
    }

    public bool MicEnabled { get => Settings.MicaEnabled; set { Settings.MicaEnabled = value; } }

    public string DefaultInstallLocation
    {
        get => Settings.DefaultInstallLocation ?? "C:\\Games\\";
        set { Settings.DefaultInstallLocation = value; }
    }

    public string LogsDirectory => _directories.LogsDirectory;

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

    public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AppSettings(DefaultInstallLocation, MicEnabled));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        DefaultInstallLocation = settings.DefaultInstallLocation;
        MicEnabled = settings.MicaEnabled;
        return SaveSettings();
    }

}
