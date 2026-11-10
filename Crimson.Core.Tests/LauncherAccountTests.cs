using Crimson.Core;
using Crimson.Providers;
using Crimson.Resources;

namespace Crimson.Tests;

public class LauncherAccountTests
{
    [Fact]
    public async Task ConnectedRowShowsTheSignedInPersonAndSignOutFallsBackToTheStoreName()
    {
        var provider = new TestLauncherProvider("ea");
        var registry = new LauncherRegistry([provider]);
        var accounts = new LauncherAuthentication(registry, new ImmediateDispatcher());
        var row = Assert.Single(accounts.Accounts);
        Assert.Equal("ea", row.DisplayName);
        Assert.False(row.HasAccountName);

        await accounts.CheckAuthStatus("ea");

        Assert.True(row.IsConnected);
        Assert.True(row.HasAccountName);
        Assert.Equal("ea account", row.AccountName);
        Assert.Equal("EA", row.AccountInitials);
        Assert.Equal(UiStrings.Connected, row.StatusText);

        await provider.Auth.Logout();
        Assert.False(row.HasAccountName);
        Assert.Equal("ea", row.AccountName);
        Assert.Equal(UiStrings.NotConnected, row.StatusText);
    }

    [Fact]
    public void EachRowCanChangeItsOwnConnectionWithoutTheShellNamingAProvider()
    {
        var first = new TestLauncherProvider("ea");
        var second = new TestLauncherProvider("ubisoft");
        second.Auth.Logout().GetAwaiter().GetResult();
        var registry = new LauncherRegistry([first, second]);
        var requested = new List<string>();
        var accounts = new LauncherAuthentication(registry, new ImmediateDispatcher());
        accounts.SignInRequested += requested.Add;

        var rows = accounts.Accounts.ToDictionary(row => row.ProviderId);
        Assert.True(rows["ea"].CanConnect == false);   // already connected
        Assert.True(rows["ubisoft"].CanConnect);

        rows["ubisoft"].ConnectCommand.Execute(null);
        Assert.Equal(["ubisoft"], requested);

        rows["ea"].DisconnectCommand.Execute(null);
        Assert.False(first.Auth.AuthenticationStatus == AuthenticationStatus.LoggedIn);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
