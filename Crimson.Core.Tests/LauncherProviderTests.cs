using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class LauncherProviderTests
{
    [Fact]
    public async Task DisconnectingOneAccountDoesNotDisconnectOtherStoreAccounts()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        var registry = new LauncherRegistry([ea, ubisoft]);
        var authentication = new LauncherAuthentication(registry, new ImmediateUiDispatcher());
        var statuses = new List<(string, AuthenticationStatus)>();
        authentication.StatusChanged += (id, status) => statuses.Add((id, status));
        await ubisoft.Auth.Logout();
        Assert.Equal([("ubisoft", AuthenticationStatus.LoggedOut)], statuses);
        Assert.True(authentication.Accounts.Single(account => account.ProviderId == "ea").IsConnected);
        Assert.False(authentication.Accounts.Single(account => account.ProviderId == "ubisoft").IsConnected);
        await authentication.Logout("ea");
        Assert.Equal(2, statuses.Count);
    }

    [Theory]
    [InlineData("ea", "offer/id:with spaces % and 雪")]
    [InlineData("ubisoft", "123")]
    public void QualifiedIdentitySurvivesBothShortcutActivationFormats(string provider, string nativeId)
    {
        var id = new GameId(provider, nativeId);
        Assert.Equal(id, GameId.Parse(id.ToString()));
        Assert.True(GameLaunchRequest.TryParse(GameLaunchRequest.CreateProtocolUri(id.ToString()), out var uriValue));
        Assert.Equal(id, GameId.Parse(uriValue));
        Assert.True(GameLaunchRequest.TryParseCommandLine(GameLaunchRequest.CreateCommandLineArgument(id.ToString()), out var cliValue));
        Assert.Equal(id, GameId.Parse(cliValue));
    }

    [Theory]
    [InlineData("../ea")]
    [InlineData("EA")]
    [InlineData("ea\n")]
    [InlineData("")]
    [InlineData("ea/other")]
    public void ProviderIdsCannotEscapeTheirStorageRoot(string providerId) =>
        Assert.Throws<ArgumentException>(() => ProviderDataPaths.ForProvider(Path.GetTempPath(), providerId));

    [Fact]
    public void ProviderDataIsIsolatedWhileLegacyEpicPathsArePreserved()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "crimson-data"));
        Assert.Equal(root, ProviderDataPaths.ForProvider(root, "epic"));
        Assert.Equal(Path.Combine(root, "providers", "ea"), ProviderDataPaths.ForProvider(root, "ea"));
        Assert.NotEqual(ProviderDataPaths.ForProvider(root, "ea"), ProviderDataPaths.ForProvider(root, "ubisoft"));
        var key = ProviderDataPaths.GameFileKey(new GameId("ea", "../../evil/id"));
        Assert.DoesNotContain("/", key);
        Assert.NotEqual(key, ProviderDataPaths.GameFileKey(new GameId("ubisoft", "../../evil/id")));
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

}
