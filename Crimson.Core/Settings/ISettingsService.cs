namespace Crimson.Core;

public sealed record AppSettings(string DefaultInstallLocation, bool MicaEnabled = false);

public interface ISettingsService
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
