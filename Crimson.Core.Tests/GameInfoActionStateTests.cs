using System.Reflection;
using Crimson.Core;
using Crimson.Resources;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crimson.Tests;

public sealed class GameInfoActionStateTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    public static IEnumerable<object[]> ActiveStates() =>
        from action in Enum.GetValues<ActionType>()
        from status in new[] { ActionStatus.Pending, ActionStatus.OnGoing, ActionStatus.Processing, ActionStatus.Paused, ActionStatus.Cancelling }
        select new object[] { action, status };

    [Theory]
    [MemberData(nameof(ActiveStates))]
    public void ActiveOperationDisablesEveryConflictingAction(ActionType action, ActionStatus status)
    {
        var viewModel = CreateViewModel(InstallState.NotInstalled);
        Apply(viewModel, "ApplyGameStatus", InstallState.NotInstalled);
        Assert.True(viewModel.IsImportEnabled);

        Apply(viewModel, "ApplyOperation", Snapshot(action, status));

        Assert.False(viewModel.IsImportEnabled);
        Assert.False(viewModel.IsPrimaryActionEnabled);
        Assert.False(viewModel.IsManageEnabled);
        Assert.True(viewModel.CanViewStore);
    }

    [Theory]
    [InlineData(InstallState.NotInstalled, true, true, false)]
    [InlineData(InstallState.Installed, true, false, true)]
    [InlineData(InstallState.NeedUpdate, true, false, true)]
    [InlineData(InstallState.Broken, true, false, true)]
    [InlineData(InstallState.Installing, false, false, false)]
    [InlineData(InstallState.Updating, false, false, false)]
    [InlineData(InstallState.Repairing, false, false, false)]
    [InlineData(InstallState.InstallationPaused, false, false, false)]
    [InlineData(InstallState.UpdatingPaused, false, false, false)]
    [InlineData(InstallState.Playing, false, false, false)]
    public void LocalStateIsSafeEvenWithoutAnOperationSnapshot(InstallState state, bool primary, bool import, bool manage)
    {
        var viewModel = CreateViewModel(state);
        Apply(viewModel, "ApplyGameStatus", state);

        Assert.Equal(primary, viewModel.IsPrimaryActionEnabled);
        Assert.Equal(import, viewModel.IsImportEnabled);
        Assert.Equal(manage, viewModel.IsManageEnabled);
    }

    [Theory]
    [InlineData(ActionStatus.Success)]
    [InlineData(ActionStatus.Failed)]
    [InlineData(ActionStatus.Cancelled)]
    public void TerminalOperationRestoresActionsFromActualLocalState(ActionStatus status)
    {
        var viewModel = CreateViewModel(InstallState.NotInstalled);
        Apply(viewModel, "ApplyGameStatus", InstallState.NotInstalled);
        Apply(viewModel, "ApplyOperation", Snapshot(ActionType.Install, ActionStatus.Processing));
        Apply(viewModel, "ApplyOperation", Snapshot(ActionType.Install, status));

        Assert.True(viewModel.IsImportEnabled);
        Assert.True(viewModel.IsPrimaryActionEnabled);
        Assert.False(viewModel.IsManageEnabled);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("installed")]
    public async Task DirectCommandsCannotBypassActiveBaseOrDlcOperation(string activeApp)
    {
        var viewModel = CreateViewModel(InstallState.NotInstalled);
        _fixture.SetCurrent(activeApp);
        var pickerCalls = 0;
        var installDialogCalls = 0;
        viewModel.FolderPickerRequested += () => { pickerCalls++; return Task.FromResult(_fixture.BasePath); };
        viewModel.ShowInstallDialogRequested += () => { installDialogCalls++; return Task.CompletedTask; };

        await viewModel.ImportGameCommand.ExecuteAsync(null);
        await viewModel.PrimaryActionCommand.ExecuteAsync(null);
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.Installed;
        viewModel.VerifyRepairCommand.Execute(null);
        viewModel.UninstallCommand.Execute(null);
        await viewModel.MoveGameCommand.ExecuteAsync(null);

        Assert.Equal(0, pickerCalls);
        Assert.Equal(0, installDialogCalls);
        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderPickerReturnCannotQueueAfterAnOperationStarts(bool move)
    {
        var viewModel = CreateViewModel(move ? InstallState.Installed : InstallState.NotInstalled);
        var folder = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.FolderPickerRequested += () => folder.Task;
        var command = move ? viewModel.MoveGameCommand : viewModel.ImportGameCommand;
        var pending = command.ExecuteAsync(null);
        _fixture.SetCurrent("installed");
        folder.SetResult(_fixture.BasePath);
        await pending;

        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderPickerReturnCannotAffectNewlyNavigatedGame(bool move)
    {
        var viewModel = CreateViewModel(move ? InstallState.Installed : InstallState.NotInstalled);
        _fixture.SetCurrent("unrelated");
        var folder = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.FolderPickerRequested += () => folder.Task;
        var pending = (move ? viewModel.MoveGameCommand : viewModel.ImportGameCommand).ExecuteAsync(null);
        viewModel.Game = _fixture.InstalledDlc;
        folder.SetResult(_fixture.BasePath);
        await pending;

        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Fact]
    public async Task RunningGameLocksPrimaryActionBehindPlayingStatus()
    {
        var viewModel = CreateViewModel(InstallState.Installed);
        await viewModel.OnNavigatedTo("base");
        Assert.True(viewModel.IsPrimaryActionEnabled);
        Assert.Equal(UiStrings.Play, viewModel.PrimaryActionButtonText);

        _fixture.Library.SetGameRunning("base", true);

        Assert.False(viewModel.IsPrimaryActionEnabled);
        Assert.Equal(UiStrings.Playing, viewModel.PrimaryActionButtonText);
        Assert.Equal(UiStrings.Playing, viewModel.GameStatusText);

        _fixture.Library.SetGameRunning("base", false);

        Assert.True(viewModel.IsPrimaryActionEnabled);
        Assert.Equal(UiStrings.Play, viewModel.PrimaryActionButtonText);
    }

    [Fact]
    public async Task RunningStateDoesNotLockGamesThatAllowMultipleInstances()
    {
        _fixture.BaseGame.Metadata.CustomAttributes = new CustomAttributes
        {
            AllowMultipleInstances = new AllowMultipleInstances { Value = "true" }
        };
        var viewModel = CreateViewModel(InstallState.Installed);
        await viewModel.OnNavigatedTo("base");

        _fixture.Library.SetGameRunning("base", true);

        Assert.True(viewModel.IsPrimaryActionEnabled);
        Assert.Equal(UiStrings.Play, viewModel.PrimaryActionButtonText);
    }

    private GameInfoViewModel CreateViewModel(InstallState state)
    {
        _fixture.BaseGame.LocalAppState!.InstallStatus = state;
        return new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, _fixture.Installer,
            _fixture.Library, _fixture.Storage, new ImmediateDispatcher()) { Game = _fixture.BaseGame };
    }

    private static OperationSnapshot Snapshot(ActionType action, ActionStatus status) => new(
        "base", action, status, OperationPhase.Transferring, 20, 20, 100, 1, null);

    private static void Apply(GameInfoViewModel viewModel, string method, object value) =>
        typeof(GameInfoViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [value]);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

    public void Dispose() => _fixture.Dispose();
}
