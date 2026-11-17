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
        return (new GameImportViewModel(new LauncherInstaller(registry), new LauncherLibrary(registry), registry), ubisoft);
    }

    private static void AddGame(TestLauncherProvider provider, string localId, string title, InstallState? state,
        bool isDlc = false, bool canInstall = true) =>
        provider.Games.Games.Add(new LauncherGame
        {
            Id = new GameId(provider.Id, localId), AppTitle = title, IsDownloadableContent = isDlc, CanInstall = canInstall,
            LocalAppState = state is { } installState ? new LauncherInstallation(installState, "C:\\Old") : null
        });

    [Fact]
    public async Task DetectedGamesSortFirstAndPrefillTheirFolder()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        AddGame(ubisoft, "2", "Zeta", InstallState.NotInstalled);
        ubisoft.Jobs.ImportCandidates.Add(new ImportCandidate("ubisoft:2", "Zeta", "E:\\Games\\Zeta"));

        await model.LoadAsync("ubisoft", CancellationToken.None);

        Assert.Equal(["ubisoft:2", "ubisoft:1"], model.Items.Select(item => item.AppName));
        Assert.Equal("ubisoft:2", model.SelectedItem!.AppName);
        Assert.Equal("E:\\Games\\Zeta", model.Location);
        Assert.True(model.CanImport);

        model.SelectedItem = model.Items[1];
        Assert.Null(model.Location);
        Assert.False(model.CanImport);
    }

    [Fact]
    public async Task InstalledUninstallableAndDlcGamesAreNotOffered()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "installed", "Installed", InstallState.Installed);
        AddGame(ubisoft, "addon", "Add-on", null, isDlc: true);
        AddGame(ubisoft, "expired", "Expired", null, canInstall: false);
        AddGame(ubisoft, "offered", "Offered", null);

        await model.LoadAsync("ubisoft", CancellationToken.None);

        Assert.Equal(["ubisoft:offered"], model.Items.Select(item => item.AppName));
    }

    [Fact]
    public async Task RejectedFolderIsNotQueuedAndExplainsWhy()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        ubisoft.Jobs.ImportFailure = new OperationFailure(OperationFailureKind.Storage, "not_this_game",
            "This folder does not contain the game.", "Choose its installation folder.", false);
        await model.LoadAsync("ubisoft", CancellationToken.None);
        model.SelectedItem = model.Items[0];
        model.FolderPickerRequested += () => Task.FromResult<string?>("D:\\Elsewhere");
        await model.PickLocationAsync();

        Assert.False(await model.ImportSelectedAsync(CancellationToken.None));

        Assert.Empty(ubisoft.Jobs.Queued);
        Assert.Contains("This folder does not contain the game.", model.StatusMessage);
    }

    [Fact]
    public async Task AcceptedFolderQueuesAnImportForThatGame()
    {
        var (model, ubisoft) = Create();
        AddGame(ubisoft, "1", "Alpha", null);
        await model.LoadAsync("ubisoft", CancellationToken.None);
        model.SelectedItem = model.Items[0];
        model.FolderPickerRequested += () => Task.FromResult<string?>("D:\\Games\\Alpha");
        await model.PickLocationAsync();

        Assert.True(await model.ImportSelectedAsync(CancellationToken.None));

        var queued = Assert.Single(ubisoft.Jobs.Queued);
        Assert.Equal(("ubisoft:1", ActionType.Import, "D:\\Games\\Alpha"), (queued.AppName, queued.Action, queued.Location));
        Assert.Equal(("ubisoft:1", "D:\\Games\\Alpha"), Assert.Single(ubisoft.Jobs.ImportValidations));
    }
}
