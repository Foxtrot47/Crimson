using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class LauncherProviderTests
{
    [Fact]
    public async Task UnifiedLibraryContainsAllThreeStoresAndRoutesDuplicateNativeIds()
    {
        var epic = new TestLauncherProvider("epic");
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        var games = new[] { epic.Games.Add("123"), ea.Games.Add("123"), ubisoft.Games.Add("123") };
        var registry = new LauncherRegistry([epic, ea, ubisoft]);
        var library = new LauncherLibrary(registry);

        Assert.Equal(games.Select(game => game.Id), (await library.GetLibraryData()).Select(game => game.Id));
        foreach (var game in games)
        {
            Assert.Equal(game.Id, library.GetGameInfo(game.AppName)!.Id);
            Assert.True(await library.LaunchApp(game.AppName));
        }
        Assert.Equal(["epic:123"], epic.Games.Launched);
        Assert.Equal(["ea:123"], ea.Games.Launched);
        Assert.Equal(["ubisoft:123"], ubisoft.Games.Launched);
        Assert.Equal(games[0].Id, library.GetGameInfo("123")!.Id); // legacy shortcut
    }

    [Fact]
    public async Task DisconnectRemovesOnlyThatAccountsGamesFromUnifiedLibrary()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        ea.Games.Add("123");
        var kept = ubisoft.Games.Add("123");
        var library = new LauncherLibrary(new LauncherRegistry([ea, ubisoft]));
        Assert.Equal(2, (await library.GetLibraryData()).Count());
        IReadOnlyList<LauncherGame> published = [];
        library.LibraryUpdated += games => published = games.ToList();
        await ea.Auth.Logout();
        Assert.Equal(kept.Id, Assert.Single(published).Id);
        Assert.Equal(kept.Id, Assert.Single(await library.GetLibraryData()).Id);
    }

    [Fact]
    public async Task BrokenStoreLibraryDoesNotHideGamesFromHealthyStore()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        ea.Games.Failure = new HttpRequestException("Store unavailable");
        var kept = ubisoft.Games.Add("123");
        var library = new LauncherLibrary(new LauncherRegistry([ea, ubisoft]));
        Assert.Equal(kept.Id, Assert.Single(await library.GetLibraryData()).Id);
    }

    [Fact]
    public async Task PreviousAccountRefreshCannotPublishAfterDisconnectAndReconnect()
    {
        var provider = new TestLauncherProvider("ea");
        var old = provider.Games.Add("123");
        provider.Games.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = new LauncherLibrary(new LauncherRegistry([provider]));
        var pending = library.GetLibraryData();
        await provider.Auth.Logout();
        provider.Auth.SignIn();
        provider.Games.Pending.SetResult([old]);
        Assert.Empty(await pending);
    }

    [Fact]
    public async Task SignedOutProviderCannotLaunchThroughAnotherSignedInAccount()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        var game = ea.Games.Add("123");
        var library = new LauncherLibrary(new LauncherRegistry([ea, ubisoft]));
        await ea.Auth.Logout();

        Assert.False(await library.LaunchApp(game.AppName));
        Assert.Empty(ea.Games.Launched);
        Assert.Equal(AuthenticationStatus.LoggedIn, ubisoft.Auth.AuthenticationStatus);
    }

    [Fact]
    public async Task ProviderCanDeclineLaunchForIndividualGames()
    {
        var provider = new TestLauncherProvider("ea");
        var game = provider.Games.Add("123", canLaunch: false);
        Assert.False(await new LauncherLibrary(new LauncherRegistry([provider])).LaunchApp(game.AppName));
        Assert.Empty(provider.Games.Launched);
    }

    [Fact]
    public void DuplicateOrUnknownProvidersAreRejectedWithoutFallingBackToEpic()
    {
        Assert.Throws<ArgumentException>(() => new LauncherRegistry([]));
        Assert.Throws<ArgumentException>(() => new LauncherRegistry([new TestLauncherProvider("ea"), new TestLauncherProvider("ea")]));
        var registry = new LauncherRegistry([new TestLauncherProvider("epic")]);
        Assert.Throws<KeyNotFoundException>(() => registry.Get("ea"));
        Assert.Throws<KeyNotFoundException>(() => new LauncherLibrary(registry).GetGameInfo("ea:123"));
        Assert.Equal("epic", Assert.Single(registry.Providers).Id);
    }

    [Fact]
    public async Task LoggedOutProviderSlowResultsCannotReturnToCombinedLibrary()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        ea.Games.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new LauncherRegistry([ea, ubisoft]);
        var library = new LauncherLibrary(registry);
        var received = new List<LauncherGame>();
        library.LibraryUpdated += games => received.AddRange(games);
        var pending = library.GetLibraryData();
        await ea.Auth.Logout();
        var old = ea.Games.Add("123");
        ea.Games.Publish();
        ea.Games.Pending.SetResult([old]);

        Assert.Empty(await pending);
        Assert.Empty(received);
    }

    [Fact]
    public void ForeignIdentitiesCannotBePublishedByAProvider()
    {
        var provider = new TestLauncherProvider("ea");
        var library = new LauncherLibrary(new LauncherRegistry([provider]));
        library.GameStatusUpdated += _ => { };
        var foreign = new LauncherGame { Id = new GameId("ubisoft", "123"), AppTitle = "Foreign" };
        Assert.Throws<InvalidOperationException>(() => provider.Games.PublishStatus(foreign));
    }

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

    [Fact]
    public void CombinedInstallationQueueKeepsEachJobsOwner()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        var registry = new LauncherRegistry([ea, ubisoft]);
        var installer = new LauncherInstaller(registry);
        installer.AddToQueue(new InstallItem("ea:123", ActionType.Install, "ea-install"));
        ea.Jobs.Start();
        installer.AddToQueue(new InstallItem("ubisoft:123", ActionType.Install, "ubisoft-install"));
        installer.PauseInstall();
        installer.CancelInstall("ea:123");

        Assert.Equal("ea:123", installer.CurrentInstall!.AppName);
        Assert.Equal(["ea:123", "ubisoft:123"], installer.GetQueueItemNames());
        Assert.Equal(1, ea.Jobs.Pauses);
        Assert.Equal(0, ubisoft.Jobs.Pauses);
        Assert.Equal(["ea:123"], ea.Jobs.Cancelled);
        Assert.Empty(ubisoft.Jobs.Cancelled);
        Assert.Throws<NotSupportedException>(() => installer.AddToQueue(new InstallItem("ubisoft:123", ActionType.Repair, "path")));
    }

    [Fact]
    public async Task OptionalInstallerAndStoreDoNotPreventLibraryOrLaunch()
    {
        var provider = new TestLauncherProvider("ea") { Installer = null };
        var game = provider.Games.Add("123");
        var registry = new LauncherRegistry([provider]);
        var installer = new LauncherInstaller(registry);
        var library = new LauncherLibrary(registry);
        Assert.Null(Assert.Single(registry.Providers).Store);
        Assert.False(installer.Supports(ActionType.Install));
        Assert.Empty(installer.GetQueueItemNames());
        Assert.True(await library.LaunchApp(game.AppName));
        Assert.Throws<NotSupportedException>(() => installer.AddToQueue(new InstallItem(game.AppName, ActionType.Install, "path")));
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
