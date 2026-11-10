using Crimson.Models;

namespace Crimson.Providers;

/// <summary>Shell-facing data. Provider protocol objects never cross this boundary.</summary>
public sealed class LauncherGame
{
    public required GameId Id { get; init; }
    public string AppName => Id.ToString();
    public required string AppTitle { get; init; }
    public string? CoverImageUrl { get; init; }
    public string? LandscapeImageUrl { get; init; }
    public bool IsDownloadableContent { get; init; }
    public bool CanLaunch { get; init; } = true;
    public LauncherInstallation? LocalAppState { get; init; }
    public bool IsDlc() => IsDownloadableContent;
}

public sealed record LauncherInstallation(InstallState InstallStatus, string InstallPath);
