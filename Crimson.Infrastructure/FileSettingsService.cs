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
        var result = AtomicJsonFile.ReadAndMigrate(_settingsPath, JsonStateSchemas.Settings);
        if (result.Status == JsonStateReadStatus.UnsupportedVersion)
            throw new NotSupportedException(
                $"Settings schema version {result.Version} is not supported.");
        if (result.Status == JsonStateReadStatus.Corrupt)
            throw new InvalidDataException($"Settings state is corrupt: {result.Error ?? "unknown error"}.");
        var stored = result.Value;
        if (!string.IsNullOrWhiteSpace(stored?.DefaultInstallLocation))
            return Task.FromResult(new AppSettings(stored.DefaultInstallLocation));

        return Task.FromResult(new AppSettings(GetDefaultInstallLocation()));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.DefaultInstallLocation))
            throw new ArgumentException("Default install location is required.", nameof(settings));
        var stored = new Settings { DefaultInstallLocation = settings.DefaultInstallLocation };
        AtomicJsonFile.Write(_settingsPath, stored, JsonStateSchemas.Settings);
        return Task.CompletedTask;
    }

    public string LogsDirectory => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "logs");

    private static string GetDefaultInstallLocation() => OperatingSystem.IsWindows()
        ? @"C:\Games"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
}
