using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class NotificationPolicyTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Theory]
    [InlineData("install_folder_save_failed", false)]
    [InlineData("desktop_shortcut_failed", false)]
    [InlineData("start_menu_shortcut_failed", false)]
    [InlineData("installation_folder_missing", false)]
    [InlineData("dlc_changes_failed", false)]
    [InlineData("epic_install_detection_failed", false)]
    [InlineData("epic_install_import_failed", false)]
    [InlineData("folder_picker_unavailable", false)]
    [InlineData("install_dialog_unavailable", false)]
    [InlineData("pending_state_invalid", true)]
    [InlineData("operation_state_save_failed", true)]
    public void OnlyUnsafePreparationFailuresRequestGlobalDialogs(string code, bool expected)
    {
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;
        _fixture.Installer.ReportPreparationFailure("base", ActionType.Install,
            new OperationFailure(OperationFailureKind.Unknown, code, "Synthetic safe message", "Try again.", false));
        Assert.Equal(expected, Assert.Single(errors).ShowErrorDialog);
    }

    [Fact]
    public void SecondaryFailuresRemainInSuccessfulHistoryWithoutBecomingTerminalFailures()
    {
        var item = new InstallItem("base", ActionType.Install, _fixture.BasePath) { Status = ActionStatus.Success };
        var history = (List<InstallItem>)typeof(InstallManager)
            .GetField("_installHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_fixture.Installer)!;
        history.Add(item);
        var failure = new OperationFailure(OperationFailureKind.Storage, "desktop_shortcut_failed", "Shortcut failed.", "Try again.", false);
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;

        _fixture.Installer.ReportSecondaryFailure(item, failure);

        Assert.False(Assert.Single(errors).ShowErrorDialog);
        var operation = Assert.Single(_fixture.Installer.GetHistoryOperations());
        Assert.Equal(ActionStatus.Success, operation.Status);
        Assert.Null(operation.Failure);
        Assert.Equal(failure, operation.Notice);
        Assert.Equal("Shortcut failed. Try again.", OperationPresenter.Present(operation).Detail);
        var previous = operation with { Notice = null };
        Assert.True(DownloadsViewModel.HasOperationListChanged([previous], [operation]));
        using var logger = new LoggerConfiguration().CreateLogger();
        using var model = new DownloadsViewModel(logger, _fixture.Installer, _fixture.Library, new ImmediateDispatcher());
        typeof(DownloadsViewModel).GetMethod("FetchHistoryItemsList", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [new[] { operation }]);
        var row = Assert.Single(model.HistoryItems);
        Assert.True(row.HasFailure);
        Assert.False(row.CanRetry);
        Assert.Equal("Shortcut failed. Try again.", row.FailureText);
        Assert.Equal(ActionStatus.Success, row.Operation!.Status);
        Assert.Contains("desktop_shortcut_failed", OperationDiagnostics.Create(row.Operation));
    }

    [Theory]
    [InlineData("base", false, true)]
    [InlineData("installed", false, true)]
    [InlineData("unrelated", false, false)]
    [InlineData("base", true, false)]
    public void GameInfoShowsOnlyNonmodalErrorsForItsOwnGame(string appName, bool modal, bool visible)
    {
        var model = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, _fixture.Installer,
            _fixture.Library, _fixture.Storage, new ImmediateDispatcher()) { Game = _fixture.BaseGame };
        var notice = Notice(appName, modal);

        Deliver(model, notice);

        Assert.Equal(visible, model.HasActionError);
        Assert.Equal(visible ? "Synthetic safe message Try again." : string.Empty, model.ActionErrorMessage);
    }

    [Fact]
    public void DelayedLocalErrorDoesNotFollowTheUserAwayFromTheOriginatingPage()
    {
        var dispatcher = new QueuedDispatcher();
        var model = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, _fixture.Installer,
            _fixture.Library, _fixture.Storage, dispatcher) { Game = _fixture.BaseGame };
        Deliver(model, Notice("base", false));
        model.OnNavigatedFrom();
        dispatcher.Drain();
        Assert.False(model.HasActionError);
    }

    private static OperationSnapshot Notice(string appName, bool modal) => OperationSnapshot.From(
        new InstallItem(appName, ActionType.Install, string.Empty)
        {
            Failure = new OperationFailure(OperationFailureKind.Unknown, "synthetic_failure", "Synthetic safe message", "Try again.", false)
        }) with { ShowErrorDialog = modal };

    private static void Deliver(GameInfoViewModel model, OperationSnapshot notice) => typeof(GameInfoViewModel)
        .GetMethod("HandleActionNotice", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [notice]);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = new();
        public bool TryEnqueue(Action action) { _pending.Enqueue(action); return true; }
        public void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }

    public void Dispose() => _fixture.Dispose();
}
