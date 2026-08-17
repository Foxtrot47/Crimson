using Crimson.Core;
using Xunit;

namespace Crimson.Tests;

public sealed class EpicLauncherWebLoginTests
{
    [Fact]
    public void UsesLauncherIdentityAndLoginEndpoint()
    {
        Assert.Equal(
            "EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live",
            EpicLauncherWebLogin.UserAgent);
        Assert.Equal(
            "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit",
            EpicLauncherWebLogin.ApiUserAgent);
        Assert.Equal("https://www.epicgames.com/id/login", EpicLauncherWebLogin.LoginUri.AbsoluteUri);
        Assert.Equal("/id/login", EpicLauncherWebLogin.LoginUri.AbsolutePath);
        Assert.Empty(EpicLauncherWebLogin.LoginUri.Query);
    }
}
