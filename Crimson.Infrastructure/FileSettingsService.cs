using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Infrastructure;

public sealed class FileSettingsService(string appDataRoot) : ISettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetFullPath(string.IsNullOrWhiteSpace(appDataRoot)
            ? throw new ArgumentException("Application data root is required.", nameof(appDataRoot))
            : appDataRoot),
        "settings.json");

    public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AtomicJsonFile.TryRead<string>(_settingsPath, out var json) &&
            !string.IsNullOrWhiteSpace(json))
        {
            var stored = JsonSerializer.Deserialize<Settings>(json);
            if (!string.IsNullOrWhiteSpace(stored?.DefaultInstallLocation))
                return Task.FromResult(new AppSettings(stored.DefaultInstallLocation));
        }

        return Task.FromResult(new AppSettings(GetDefaultInstallLocation()));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.DefaultInstallLocation))
            throw new ArgumentException("Default install location is required.", nameof(settings));
        var stored = new Settings { DefaultInstallLocation = settings.DefaultInstallLocation };
        AtomicJsonFile.Write(_settingsPath, JsonSerializer.Serialize(stored));
        return Task.CompletedTask;
    }

    public string LogsDirectory => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "logs");

    private static string GetDefaultInstallLocation() => OperatingSystem.IsWindows()
        ? @"C:\Games"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
}
