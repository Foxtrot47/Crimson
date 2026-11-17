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
    public async Task CombinedLibraryCardsCarryStoreNamesWithoutEpicMetadata()
    {
        var ea = new TestLauncherProvider("ea");
        var ubisoft = new TestLauncherProvider("ubisoft");
        ea.Games.Add("123");
        ubisoft.Games.Add("123");
        var registry = new LauncherRegistry([ea, ubisoft]);
        using var log = new Serilog.LoggerConfiguration().CreateLogger();
        var vm = new LibraryViewModel(log, new LauncherInstaller(registry), new LauncherLibrary(registry), registry, new ImmediateUiDispatcher());
        await vm.OnNavigatedTo(null!);
        Assert.Equal(2, vm.GamesList.Count);
        Assert.Equal(["ea", "ubisoft"], vm.GamesList.Select(item => item.ProviderName).Order().ToList());
        Assert.False(vm.ShowEmptyLibrary);
        vm.OnNavigatedFrom();
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

    [Fact]
    public async Task NeutralGamePresentationHonorsProviderCapabilities()
    {
        var provider = new TestLauncherProvider("ea");
        var game = provider.Games.Add("123");
        var registry = new LauncherRegistry([provider]);
        var vm = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance,
            new LauncherInstaller(registry), new LauncherLibrary(registry), registry, new ImmediateUiDispatcher());
        await vm.OnNavigatedTo(game.AppName);
        Assert.Equal(game.Id, vm.Game!.Id);
        Assert.Equal(game.CoverImageUrl, vm.CoverImageUrl);
        Assert.True(vm.IsPrimaryActionEnabled);
        // Unsupported actions stay inert instead of reaching a provider that cannot perform them.
        vm.VerifyRepairCommand.Execute(null);
        vm.UninstallCommand.Execute(null);
        Assert.Empty(provider.Jobs.Queued);
        vm.OnNavigatedFrom();
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

    [Fact]
    public async Task LanguageAndImportCallsReachOnlyTheOwningProviderWithQualifiedIds()
    {
        var epic = new TestLauncherProvider("epic");
        var ubisoft = new TestLauncherProvider("ubisoft");
        var choice = new InstallLanguageChoice([new("en-US", "English"), new("fr-FR", "French")], ["en-US"], true);
        ubisoft.Jobs.LanguageChoice = choice;
        ubisoft.Jobs.ImportFailure = new OperationFailure(OperationFailureKind.Storage, "wrong_folder", "Wrong folder.", "Pick another.", false);
        var installer = new LauncherInstaller(new LauncherRegistry([epic, ubisoft]));

        Assert.Same(choice, await installer.GetInstallLanguages("ubisoft:2688"));
        Assert.Equal((200d, 400d), await installer.GetSizesForLanguages("ubisoft:2688", ["en-US", "fr-FR"]));
        Assert.Equal("wrong_folder", (await installer.ValidateImportLocation("ubisoft:2688", "D:\\Games\\WD2"))!.Code);
        Assert.Null(await installer.GetInstallLanguages("epic:abc"));

        Assert.Equal(["ubisoft:2688"], ubisoft.Jobs.LanguageRequests);
        Assert.Equal(("ubisoft:2688", "D:\\Games\\WD2"), Assert.Single(ubisoft.Jobs.ImportValidations));
        Assert.Equal(["en-US", "fr-FR"], Assert.Single(ubisoft.Jobs.SizeRequests).Languages);
        Assert.Equal(["epic:abc"], epic.Jobs.LanguageRequests);
        Assert.Empty(epic.Jobs.SizeRequests);
        Assert.Empty(epic.Jobs.ImportValidations);
    }

    [Fact]
    public async Task ImportCandidatesAreScopedToTheRequestedProviderAndCannotClaimForeignGames()
    {
        var epic = new TestLauncherProvider("epic");
        var ubisoft = new TestLauncherProvider("ubisoft");
        epic.Jobs.ImportCandidates.Add(new ImportCandidate("epic:abc", "Epic game", "C:\\Epic"));
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:2688", "Watch Dogs 2", "E:\\WD2"));
        var installer = new LauncherInstaller(new LauncherRegistry([epic, ubisoft]));

        Assert.Equal("ubisoft:2688", Assert.Single(await installer.DiscoverImportCandidates("ubisoft")).AppName);
        Assert.Equal(2, (await installer.DiscoverImportCandidates()).Count);

        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("epic:abc", "Spoofed", "C:\\Epic"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.DiscoverImportCandidates("ubisoft"));
    }

    [Fact]
    public async Task ProviderWithoutInstallerOffersNoLanguagesOrImports()
    {
        var provider = new TestLauncherProvider("ea") { Installer = null };
        var installer = new LauncherInstaller(new LauncherRegistry([provider]));

        Assert.Null(await installer.GetInstallLanguages("ea:1"));
        Assert.Null(await installer.ValidateImportLocation("ea:1", "C:\\Games"));
        Assert.Empty(await installer.DiscoverImportCandidates("ea"));
        Assert.False(installer.SupportsImport("ea"));
    }

    [Fact]
    public void SupportsImportFollowsTheProvidersInstaller()
    {
        var epic = new TestLauncherProvider("epic");
        var ubisoft = new TestLauncherProvider("ubisoft");
        ubisoft.Jobs.Actions.Add(ActionType.Import);
        var installer = new LauncherInstaller(new LauncherRegistry([epic, ubisoft]));

        Assert.True(installer.SupportsImport("ubisoft"));
        Assert.False(installer.SupportsImport("epic"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LanguageChangesQueueOnlyWhenTheProviderSupportsThem(bool supported)
    {
        var provider = new TestLauncherProvider("ubisoft");
        var game = provider.Games.Add("2688");
        if (supported) provider.Jobs.Actions.UnionWith([ActionType.Repair]);
        var registry = new LauncherRegistry([provider]);
        var vm = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance,
            new LauncherInstaller(registry), new LauncherLibrary(registry), registry, new ImmediateUiDispatcher());
        await vm.OnNavigatedTo(game.AppName);

        vm.ChangeLanguagesCommand.Execute(new List<string> { "fr-FR" });
        vm.ChangeLanguagesCommand.Execute(new List<string>());

        if (!supported)
        {
            Assert.Empty(provider.Jobs.Queued);
            return;
        }
        Assert.Collection(provider.Jobs.Queued,
            repair =>
            {
                Assert.Equal(ActionType.Repair, repair.Action);
                Assert.Equal(["fr-FR"], repair.Languages!);
                Assert.Equal(game.LocalAppState!.InstallPath, repair.Location);
            });
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task ImportFolderRejectedByTheProviderIsNotQueued()
    {
        var provider = new TestLauncherProvider("ubisoft");
        var game = provider.Games.Add("2688", InstallState.NotInstalled);
        provider.Jobs.Actions.Add(ActionType.Import);
        provider.Jobs.ImportFailure = new OperationFailure(OperationFailureKind.Storage, "not_this_game", "Not this game.", "Pick its folder.", false);
        var registry = new LauncherRegistry([provider]);
        var vm = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance,
            new LauncherInstaller(registry), new LauncherLibrary(registry), registry, new ImmediateUiDispatcher());
        await vm.OnNavigatedTo(game.AppName);
        vm.FolderPickerRequested += () => Task.FromResult("D:\\Elsewhere");

        await vm.ImportGameCommand.ExecuteAsync(null);
        Assert.Empty(provider.Jobs.Queued);
        Assert.Equal("not_this_game", Assert.Single(provider.Jobs.Failures).Failure.Code);

        provider.Jobs.ImportFailure = null;
        await vm.ImportGameCommand.ExecuteAsync(null);
        var queued = Assert.Single(provider.Jobs.Queued);
        Assert.Equal((ActionType.Import, "D:\\Elsewhere"), (queued.Action, queued.Location));
        vm.OnNavigatedFrom();
    }
    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

}
