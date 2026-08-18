using Crimson.Core;
using Crimson.Models;

namespace Crimson.Infrastructure;

public sealed class FileSettingsService(string appDataRoot) : ISettingsService, ISettingsStore
{
    private readonly object _writeGate = new();
    private readonly string _settingsPath = Path.Combine(
        Path.GetFullPath(string.IsNullOrWhiteSpace(appDataRoot)
            ? throw new ArgumentException("Application data root is required.", nameof(appDataRoot))
            : appDataRoot),
        "settings.json");

    public Settings? Get()
    {
        var result = AtomicJsonFile.ReadAndMigrate(_settingsPath, JsonStateSchemas.Settings);
        return result.Status switch
        {
            JsonStateReadStatus.Success => result.Value,
            JsonStateReadStatus.Missing => null,
            JsonStateReadStatus.UnsupportedVersion => throw new NotSupportedException(
                $"Settings schema version {result.Version} is not supported."),
            _ => throw new InvalidDataException(
                $"Settings state is corrupt: {result.Error ?? "unknown error"}.")
        };
    }

    public void Save(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_writeGate)
            AtomicJsonFile.Write(_settingsPath, settings, JsonStateSchemas.Settings);
    }

    public Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = Get();
        return Task.FromResult(new AppSettings(
            string.IsNullOrWhiteSpace(stored?.DefaultInstallLocation)
                ? GetDefaultInstallLocation()
                : stored.DefaultInstallLocation,
            stored?.MicaEnabled ?? false));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.DefaultInstallLocation))
            throw new ArgumentException("Default install location is required.", nameof(settings));
        var stored = Get() ?? new Settings();
        stored.DefaultInstallLocation = settings.DefaultInstallLocation;
        stored.MicaEnabled = settings.MicaEnabled;
        Save(stored);
        return Task.CompletedTask;
    }

    public string LogsDirectory => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "logs");

    private static string GetDefaultInstallLocation() => OperatingSystem.IsWindows()
        ? @"C:\Games"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
}
