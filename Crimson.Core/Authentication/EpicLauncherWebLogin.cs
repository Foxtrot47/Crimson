namespace Crimson.Core;

public static class EpicLauncherWebLogin
{
    public const string UserAgent = "EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live";

    public const string ApiUserAgent =
        "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    public static Uri LoginUri { get; } = new("https://www.epicgames.com/id/login");
}
