using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;

namespace Crimson.Tests;

public sealed class TrayMenuPresenterTests
{
    [Theory]
    [InlineData(InstallState.NotInstalled, false, false)]
    [InlineData(InstallState.Installed, true, true)]
    [InlineData(InstallState.NeedUpdate, true, true)]
    [InlineData(InstallState.Broken, true, false)]
    [InlineData(InstallState.Playing, true, false)]
    [InlineData(InstallState.Installing, true, false)]
    [InlineData(InstallState.InstallationPaused, true, false)]
    [InlineData(InstallState.Updating, true, false)]
    [InlineData(InstallState.UpdatingPaused, true, false)]
    [InlineData(InstallState.Repairing, true, false)]
    public void InstalledStatesControlListingAndLaunch(InstallState state, bool listed, bool canLaunch)
    {
        var items = TrayMenuPresenter.GetInstalledGames([Game("base", "Game", state)], true, _ => false);

        Assert.Equal(listed ? 1 : 0, items.Count);
        if (listed) Assert.Equal(canLaunch, Assert.Single(items).CanLaunch);
    }

    [Fact]
    public void LoggedOutDoesNotReadGamesOrOperations()
    {
        var items = TrayMenuPresenter.GetInstalledGames(null!, false,
            _ => throw new InvalidOperationException("Must not inspect logged-out operations."));

        Assert.Empty(items);
    }

    [Fact]
    public void DlcAndMissingInstallationStatesAreExcluded()
    {
        var dlc = Game("dlc", "DLC", isDlc: true);
        var missing = new LauncherGame
        {
            Id = new GameId("epic", "missing"),
            AppTitle = "Missing",
            LocalAppState = null
        };

        Assert.Empty(TrayMenuPresenter.GetInstalledGames([dlc, missing], true, _ => false));
    }

    [Theory]
    [InlineData(InstallState.Installed)]
    [InlineData(InstallState.NeedUpdate)]
    public void RelatedOperationDisablesOtherwiseLaunchableGame(InstallState state)
    {
        var items = TrayMenuPresenter.GetInstalledGames(
            [Game("base", "Base", state), Game("other", "Other")], true, app => app == "epic:base");

        Assert.False(items.Single(item => item.AppName == "epic:base").CanLaunch);
        Assert.True(items.Single(item => item.AppName == "epic:other").CanLaunch);
    }

    [Fact]
    public void SortingDeduplicatesAppIdentityAndUsesFallbackTitles()
    {
        var items = TrayMenuPresenter.GetInstalledGames(
            [Game("z", "Zulu"), Game("b", "alpha"), Game("a", "alpha"), Game("c", " "), Game("z", "Duplicate")],
            true, _ => false);

        Assert.Equal(["epic:a", "epic:b", "epic:c", "epic:z"], items.Select(item => item.AppName));
        Assert.Equal("epic:c", items.Single(item => item.AppName == "epic:c").Title);
    }

    [Fact]
    public void EquivalentUpdatesProduceEqualMenuDescriptors()
    {
        var first = TrayMenuPresenter.GetInstalledGames([Game("base", "Game")], true, _ => false);
        var unchanged = TrayMenuPresenter.GetInstalledGames([Game("base", "Game")], true, _ => false);
        var blocked = TrayMenuPresenter.GetInstalledGames([Game("base", "Game")], true, _ => true);

        Assert.True(first.SequenceEqual(unchanged));
        Assert.False(first.SequenceEqual(blocked));
    }

    [Theory]
    [InlineData("Game", "Game")]
    [InlineData("A & B", "A && B")]
    [InlineData("A && B", "A &&&& B")]
    public void AmpersandsRemainLiteralInNativeMenu(string title, string expected)
    {
        Assert.Equal(expected, TrayMenuPresenter.EscapeMenuText(title));
    }

    private static LauncherGame Game(string appName, string title, InstallState state = InstallState.Installed,
        bool isDlc = false) => new()
    {
        Id = new GameId("epic", appName),
        AppTitle = title,
        IsDownloadableContent = isDlc,
        LocalAppState = new LauncherInstallation(state, Path.GetTempPath())
    };
}
