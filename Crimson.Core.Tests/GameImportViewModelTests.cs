using Crimson.Models;
using Crimson.Providers;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class GameImportViewModelTests
{
    private static (GameImportViewModel Model, TestLauncherProvider Ubisoft) Create()
    {
        var epic = new TestLauncherProvider("epic");
        epic.Games.Add("epic-game", InstallState.NotInstalled);
        epic.Jobs.Actions.Add(ActionType.Import);
        var ubisoft = new TestLauncherProvider("ubisoft");
        ubisoft.Jobs.Actions.Add(ActionType.Import);
        var registry = new LauncherRegistry([epic, ubisoft]);
        return (new GameImportViewModel(new LauncherInstaller(registry), new LauncherLibrary(registry)), ubisoft);
    }

    private static void AddGame(TestLauncherProvider provider, string localId, string title, InstallState? state,
        bool isDlc = false, bool canInstall = true) =>
        provider.Games.Games.Add(new LauncherGame
        {
            Id = new GameId(provider.Id, localId), AppTitle = title, IsDownloadableContent = isDlc, CanInstall = canInstall,
            LocalAppState = state is { } installState ? new LauncherInstallation(installState, "C:\\Old") : null
        });

    [Fact]
    public async Task OnlyTheInstallsTheStoreReportsAreOffered()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        AddGame(ubisoft, "2", "Zeta", InstallState.NotInstalled);
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:2", "Zeta", "E:\\Games\\Zeta"));

        await model.LoadAsync("ubisoft", CancellationToken.None);

        var choice = Assert.Single(model.Items);
        Assert.Equal(("ubisoft:2", "Zeta", "E:\\Games\\Zeta"), (choice.AppName, choice.Title, choice.DetectedPath));
        Assert.Equal("ubisoft:2", model.SelectedItem!.AppName);
        Assert.True(model.CanImport);
    }

    [Fact]
    public async Task LibraryRulesOutInstalledDlcAndUninstallableInstallsButKeepsUnknownOnes()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "installed", "Installed", InstallState.Installed);
        AddGame(ubisoft, "addon", "Add-on", null, isDlc: true);
        AddGame(ubisoft, "expired", "Expired", null, canInstall: false);
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:installed", "Installed", "E:\\Installed"));
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:addon", "Add-on", "E:\\Addon"));
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:expired", "Expired", "E:\\Expired"));
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:unknown", "Ubisoft 7", "E:\\Unknown"));

        await model.LoadAsync("ubisoft", CancellationToken.None);

        var choice = Assert.Single(model.Items);
        Assert.Equal(("ubisoft:unknown", "Ubisoft 7"), (choice.AppName, choice.Title));
    }

    [Fact]
    public async Task RejectedDetectedFolderIsNotQueuedAndExplainsWhy()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:1", "Alpha", "E:\\Games\\Alpha"));
        ubisoft.Jobs.ImportFailure = new OperationFailure(OperationFailureKind.Storage, "not_this_game",
            "This folder does not contain the game.", "Choose its installation folder.", false);
        await model.LoadAsync("ubisoft", CancellationToken.None);

        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));

        Assert.Empty(ubisoft.Jobs.Queued);
        Assert.Contains("This folder does not contain the game.", model.StatusMessage);
    }

    [Fact]
    public async Task DetectedFolderQueuesAnImportForThatGame()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:1", "Alpha", "E:\\Games\\Alpha"));
        await model.LoadAsync("ubisoft", CancellationToken.None);

        Assert.True(await model.ImportSelectedAsync(CancellationToken.None));

        var queued = Assert.Single(ubisoft.Jobs.Queued);
        Assert.Equal(("ubisoft:1", ActionType.Import, "E:\\Games\\Alpha"), (queued.AppName, queued.Action, queued.Location));
        Assert.Equal(("ubisoft:1", "E:\\Games\\Alpha"), Assert.Single(ubisoft.Jobs.ImportValidations));
    }

    [Fact]
    public async Task AStoreReportingNoInstallsOffersNothingToImport()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);

        await model.LoadAsync("ubisoft", CancellationToken.None);

        Assert.Empty(model.Items);
        Assert.Null(model.SelectedItem);
        Assert.False(model.CanImport);
        Assert.True(model.HasStatusMessage);
    }
}
