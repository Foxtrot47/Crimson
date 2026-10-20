using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class LibraryFilterTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly List<LibraryViewModel> _models = [];

    public LibraryFilterTests()
    {
        var uninstalled = _fixture.Library.GetGameInfo("available");
        uninstalled.Metadata.MainGameItem = null!;
        uninstalled.AppTitle = "Another game";
        typeof(LibraryManager).GetField("_lastUpdateDateTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Library, DateTime.Now);
    }

    [Fact]
    public async Task DefaultsToAllOwnedBaseGamesAndRestoresThemWhenFilterIsCleared()
    {
        var model = Model();
        await model.OnNavigatedTo(null!);
        Assert.False(model.InstalledOnly);
        Assert.Equal(["available", "base"], model.GamesList.Select(game => game.Name));
        model.InstalledOnly = true;
        Assert.Equal("base", Assert.Single(model.GamesList).Name);
        model.InstalledOnly = false;
        Assert.Equal(["available", "base"], model.GamesList.Select(game => game.Name));
        Assert.False(model.HasNoInstalledGames);
    }

    [Theory]
    [InlineData(InstallState.NotInstalled, false)]
    [InlineData(InstallState.Installed, true)]
    [InlineData(InstallState.NeedUpdate, true)]
    [InlineData(InstallState.Broken, true)]
    [InlineData(InstallState.Playing, true)]
    [InlineData(InstallState.Installing, true)]
    [InlineData(InstallState.InstallationPaused, true)]
    [InlineData(InstallState.Updating, true)]
    [InlineData(InstallState.UpdatingPaused, true)]
    [InlineData(InstallState.Repairing, true)]
    [InlineData((InstallState)999, false)]
    public async Task UsesTheSameTrackedInstallationStatesAsTheSidebar(InstallState state, bool expected)
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = state;
        var model = Model();
        await model.OnNavigatedTo(null!);
        model.InstalledOnly = true;
        Assert.Equal(expected ? 1 : 0, model.GamesList.Count);
        Assert.Equal(!expected, model.HasNoInstalledGames);
        Assert.DoesNotContain(model.GamesList, game => game.Name == "installed");
    }

    [Fact]
    public async Task UsesCurrentStoredStateInsteadOfAStaleGameReference()
    {
        var model = Model();
        await model.OnNavigatedTo(null!);
        var stale = _fixture.BaseGame.LocalAppState;
        _fixture.Storage.AddToLocalAppState("base", new LocalAppState
        {
            AppName = "base", InstallStatus = InstallState.NotInstalled
        });
        Assert.Equal(InstallState.Installed, stale!.InstallStatus);

        model.InstalledOnly = true;

        Assert.Empty(model.GamesList);
        Assert.True(model.HasNoInstalledGames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAndUninstallCompletionRefreshTheFilteredListWithoutTogglingAgain(bool listView)
    {
        var model = Model();
        model.IsListView = listView;
        await model.OnNavigatedTo(null!);
        model.InstalledOnly = true;
        Assert.Equal("base", Assert.Single(model.GamesList).Name);
        _fixture.Storage.AddToLocalAppState("available", new LocalAppState
        {
            AppName = "available", InstallStatus = InstallState.Installed, InstallPath = _fixture.BasePath
        });

        PublishCompletion("available", ActionType.Import);

        Assert.Equal(["available", "base"], model.GamesList.Select(game => game.Name));
        _fixture.Storage.AddToLocalAppState("base", new LocalAppState
        {
            AppName = "base", InstallStatus = InstallState.NotInstalled
        });

        PublishCompletion("base", ActionType.Uninstall);

        Assert.Equal("available", Assert.Single(model.GamesList).Name);
        Assert.True(model.InstalledOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesRelatedDlcOperationPresentationOnFilteredBaseGames(bool listView)
    {
        _fixture.SetCurrent("installed");
        var model = Model();
        model.IsListView = listView;
        await model.OnNavigatedTo(null!);
        model.InstalledOnly = true;
        var game = Assert.Single(model.GamesList);
        Assert.Equal("base", game.Name);
        Assert.True(game.HasActiveOperation);
        Assert.NotEmpty(game.OperationStatus);
    }

    [Fact]
    public async Task ChoosingFilterBeforeLoadingDoesNotDismissLoadingOrShowAnEmptyResultEarly()
    {
        var model = Model();
        model.InstalledOnly = true;
        Assert.True(model.ShowLoadingScreen);
        Assert.False(model.HasNoInstalledGames);
        await model.OnNavigatedTo(null!);
        Assert.False(model.ShowLoadingScreen);
        Assert.Equal("base", Assert.Single(model.GamesList).Name);
    }

    [Fact]
    public async Task DelayedDispatcherCallbacksUseTheLatestFilterChoice()
    {
        var dispatcher = new QueuedDispatcher();
        var model = Model(dispatcher);
        await model.OnNavigatedTo(null!);
        dispatcher.Drain();
        model.InstalledOnly = true;
        model.InstalledOnly = false;
        dispatcher.Drain();
        Assert.Equal(["available", "base"], model.GamesList.Select(game => game.Name));
    }

    [Fact]
    public async Task NavigatingAwayDiscardsQueuedFilterRefreshes()
    {
        var dispatcher = new QueuedDispatcher();
        var model = Model(dispatcher);
        await model.OnNavigatedTo(null!);
        dispatcher.Drain();
        var previous = model.GamesList;
        model.InstalledOnly = true;
        model.OnNavigatedFrom();
        dispatcher.Drain();
        Assert.Same(previous, model.GamesList);
        model.InstalledOnly = false;
        Assert.Empty(dispatcher.Pending);
    }

    [Fact]
    public async Task SwitchingViewsPreservesTheCurrentFilterAndDoesNotRebuildTheLibrary()
    {
        var model = Model();
        await model.OnNavigatedTo(null!);
        Assert.True(model.IsGridView);
        Assert.False(model.IsListView);
        model.InstalledOnly = true;
        var filtered = model.GamesList;
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        model.IsListView = true;

        Assert.False(model.IsGridView);
        Assert.True(model.InstalledOnly);
        Assert.Same(filtered, model.GamesList);
        Assert.Contains(nameof(LibraryViewModel.IsGridView), notifications);
        Assert.Equal("base", Assert.Single(model.GamesList).Name);
        model.InstalledOnly = false;
        var all = model.GamesList;
        Assert.True(model.IsListView);
        Assert.Equal(["available", "base"], all.Select(game => game.Name));

        model.IsListView = false;

        Assert.True(model.IsGridView);
        Assert.Same(all, model.GamesList);
        Assert.False(model.InstalledOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressUpdatesPreserveTheListAndEveryExistingCard(bool listView)
    {
        _fixture.SetCurrent("base");
        var install = _fixture.Installer.CurrentInstall!;
        install.Phase = OperationPhase.Transferring;
        install.ProgressPercentage = 10;
        var model = Model();
        model.IsListView = listView;
        await model.OnNavigatedTo(null!);
        var list = model.GamesList;
        var active = list.Single(item => item.Name == "base");
        var other = list.Single(item => item.Name == "available");
        var listChanges = 0;
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.GamesList)) listChanges++; };

        install.ProgressPercentage = 40;
        PublishOperation(install);

        Assert.Same(list, model.GamesList);
        Assert.Same(active, model.GamesList.Single(item => item.Name == "base"));
        Assert.Same(other, model.GamesList.Single(item => item.Name == "available"));
        Assert.Equal(40, active.OperationProgress);
        Assert.Equal(0, listChanges);
        var changes = new List<string?>();
        Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(active).PropertyChanged +=
            (_, args) => changes.Add(args.PropertyName);

        install.ProgressPercentage = 65;
        PublishOperation(install);

        Assert.Equal(65, active.OperationProgress);
        Assert.Equal([nameof(LibraryItem.OperationProgress)], changes);
        Assert.Same(list, model.GamesList);
        changes.Clear();
        PublishOperation(install);
        Assert.Empty(changes);
        install.Status = ActionStatus.Paused;
        PublishOperation(install);
        Assert.Equal("Install paused", active.OperationStatus);
        typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(_fixture.Installer, null);
        PublishCompletion("base", ActionType.Install);
        Assert.Same(list, model.GamesList);
        Assert.False(active.HasActiveOperation);
        Assert.Empty(active.OperationStatus);
    }

    private void PublishOperation(InstallItem install) =>
        typeof(InstallManager).GetMethod("PublishOperation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_fixture.Installer, [install]);

    private LibraryViewModel Model(IUiDispatcher? dispatcher = null)
    {
        var model = new LibraryViewModel(_logger, _fixture.Installer, _fixture.Library,
            dispatcher ?? new ImmediateDispatcher());
        _models.Add(model);
        return model;
    }

    private void PublishCompletion(string appName, ActionType action) =>
        typeof(InstallManager).GetMethod("PublishOperation", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_fixture.Installer, [new InstallItem(appName, action, _fixture.BasePath)
            {
                Status = ActionStatus.Success, Phase = OperationPhase.Finalizing
            }]);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        public Queue<Action> Pending { get; } = new();
        public bool TryEnqueue(Action action) { Pending.Enqueue(action); return true; }
        public void Drain()
        {
            while (Pending.TryDequeue(out var action)) action();
        }
    }

    public void Dispose()
    {
        foreach (var model in _models) model.OnNavigatedFrom();
        _fixture.Dispose();
        _logger.Dispose();
    }
}
