using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Resources;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class LibraryPrimaryActionTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly List<LibraryViewModel> _models = [];

    public LibraryPrimaryActionTests()
    {
        // Both fixture games belong to the library as base games, one installed and one owned only.
        _fixture.Library.GetGameInfo("available").Metadata.MainGameItem = null!;
        typeof(EpicLibraryManager).GetField("_lastUpdateDateTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Library, DateTimeOffset.Now);
    }

    [Theory]
    [InlineData(InstallState.NotInstalled, GamePrimaryAction.Install, "Install")]
    [InlineData(InstallState.Installed, GamePrimaryAction.Play, "Play")]
    [InlineData(InstallState.NeedUpdate, GamePrimaryAction.Update, "Update")]
    [InlineData(InstallState.Broken, GamePrimaryAction.Repair, "Repair")]
    [InlineData(InstallState.Playing, GamePrimaryAction.None, "")]
    public async Task CardsOfferTheActionTheirInstallStateImplies(
        InstallState state, GamePrimaryAction expectedAction, string expectedText)
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = state;
        var model = Model();
        await model.OnNavigatedTo(null!);

        var card = Assert.Single(model.GamesList, item => item.Name == "epic:base");
        Assert.Equal(expectedAction, card.PrimaryAction);
        Assert.Equal(expectedText, card.PrimaryActionText);
        Assert.Equal(expectedAction != GamePrimaryAction.None, card.HasPrimaryAction);
        Assert.Equal(expectedAction != GamePrimaryAction.None, card.IsPrimaryActionEnabled);
        Assert.NotNull(card.PrimaryActionCommand);
    }

    [Fact]
    public async Task OwnedButMissingGameOffersInstallWhileInstalledGameOffersPlay()
    {
        var model = Model();
        await model.OnNavigatedTo(null!);

        var installable = Assert.Single(model.GamesList, item => item.Name == "epic:available");
        Assert.Equal(GamePrimaryAction.Install, installable.PrimaryAction);
        Assert.Equal(UiStrings.Install, installable.PrimaryActionText);
        var installed = Assert.Single(model.GamesList, item => item.Name == "epic:base");
        Assert.Equal(GamePrimaryAction.Play, installed.PrimaryAction);
        Assert.Equal(UiStrings.Play, installed.PrimaryActionText);
    }

    [Fact]
    public async Task RunningOperationTakesTheCardActionAway()
    {
        _fixture.SetCurrent("base");
        var model = Model();
        await model.OnNavigatedTo(null!);

        var card = Assert.Single(model.GamesList, item => item.Name == "epic:base");
        Assert.True(card.HasActiveOperation);
        Assert.Equal(GamePrimaryAction.None, card.PrimaryAction);
        Assert.False(card.HasPrimaryAction);
        Assert.False(card.IsPrimaryActionEnabled);
    }

    [Fact]
    public async Task InstallAsksThePageForTheInstallDialog()
    {
        var requested = new List<LauncherGame>();
        var model = Model();
        model.ShowInstallDialogRequested += game =>
        {
            requested.Add(game);
            return Task.CompletedTask;
        };
        await model.OnNavigatedTo(null!);

        var card = Assert.Single(model.GamesList, item => item.Name == "epic:available");
        await card.PrimaryActionCommand!.ExecuteAsync(null);

        Assert.Equal("epic:available", Assert.Single(requested).AppName);
        Assert.Empty(_fixture.Queue());
    }

    [Fact]
    public async Task UpdateQueuesWithTheInstalledPath()
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.NeedUpdate;
        // Another game is already processing, so the queued update stays inspectable.
        _fixture.SetCurrent("available");
        var model = Model();
        await model.OnNavigatedTo(null!);

        var card = Assert.Single(model.GamesList, item => item.Name == "epic:base");
        await card.PrimaryActionCommand!.ExecuteAsync(null);

        var queued = Assert.Single(_fixture.Queue());
        Assert.Equal("base", queued.AppName);
        Assert.Equal(ActionType.Update, queued.Action);
        Assert.Equal(_fixture.BasePath, queued.Location);
    }

    [Fact]
    public async Task MissingInstallDialogHandlerReportsThePreparationFailure()
    {
        var errors = new List<OperationSnapshot>();
        _fixture.Shared.Installer.OperationError += errors.Add;
        var model = Model();
        await model.OnNavigatedTo(null!);

        var card = Assert.Single(model.GamesList, item => item.Name == "epic:available");
        await card.PrimaryActionCommand!.ExecuteAsync(null);

        Assert.Empty(_fixture.Queue());
        var failure = Assert.Single(errors);
        Assert.Equal("epic:available", failure.AppName);
        Assert.Equal(ActionType.Install, failure.Action);
        Assert.Equal("install_dialog_unavailable", failure.Failure?.Code);
    }

    private LibraryViewModel Model()
    {
        var model = new LibraryViewModel(_logger, _fixture.Shared.Installer, _fixture.Shared.Library, _fixture.Shared.Registry,
            new ImmediateDispatcher());
        _models.Add(model);
        return model;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    public void Dispose()
    {
        foreach (var model in _models) model.OnNavigatedFrom();
        _fixture.Dispose();
        _logger.Dispose();
    }
}
