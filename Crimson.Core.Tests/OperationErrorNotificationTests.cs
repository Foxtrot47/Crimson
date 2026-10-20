using System.Reflection;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class OperationErrorNotificationTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    public static IEnumerable<object[]> FailureSteps() =>
        from action in Enum.GetValues<ActionType>()
        from phase in Enum.GetValues<OperationPhase>()
        select new object[] { action, phase };

    [Theory]
    [MemberData(nameof(FailureSteps))]
    public void EveryActionAndPhasePublishesOneReportableTerminalFailure(ActionType action, OperationPhase phase)
    {
        var errors = CaptureErrors();
        var item = new InstallItem("base", action, _fixture.BasePath)
        {
            Status = ActionStatus.Failed,
            Phase = phase,
            Failure = OperationFailureClassifier.Classify(new UnauthorizedAccessException("synthetic-private-path")),
            StatusMessage = "synthetic-sensitive-raw-detail"
        };

        Publish(item);
        Publish(item);

        var error = Assert.Single(errors);
        Assert.True(error.ShowErrorDialog);
        Assert.Equal(action, error.Action);
        Assert.Equal(phase, error.Phase);
        Assert.Equal("permission_denied", error.Failure?.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Failure?.Message));
        var diagnostics = OperationDiagnostics.Create(error);
        Assert.DoesNotContain("synthetic-private-path", diagnostics);
        Assert.DoesNotContain("synthetic-sensitive-raw-detail", diagnostics);
        Assert.DoesNotContain(_fixture.BasePath, diagnostics);
        Assert.Contains("permission_denied", OperationDiagnostics.CreateIssueUri(error).ToString());
    }

    [Fact]
    public void ANewRetryCanReportTheSameErrorAgain()
    {
        var errors = CaptureErrors();
        Publish(new InstallItem("base", ActionType.Install, _fixture.BasePath) { Status = ActionStatus.Failed });
        Publish(new InstallItem("base", ActionType.Install, _fixture.BasePath) { Status = ActionStatus.Failed });
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal("operation_failed", error.Failure?.Code));
    }

    [Theory]
    [InlineData(ActionStatus.Pending)]
    [InlineData(ActionStatus.Processing)]
    [InlineData(ActionStatus.Paused)]
    [InlineData(ActionStatus.Cancelling)]
    [InlineData(ActionStatus.Cancelled)]
    [InlineData(ActionStatus.Success)]
    public void NonFailureStatesDoNotShowFailureDialogs(ActionStatus status)
    {
        var errors = CaptureErrors();
        Publish(new InstallItem("base", ActionType.Install, _fixture.BasePath) { Status = status });
        Assert.Empty(errors);
    }

    [Fact]
    public void SetupFailuresAreReportableBeforeAnOperationIsQueued()
    {
        var errors = CaptureErrors();
        _fixture.Installer.ReportPreparationFailure("base", ActionType.Import, new IOException("synthetic-private-path"));

        var error = Assert.Single(errors);
        Assert.False(error.ShowErrorDialog);
        Assert.Equal(ActionType.Import, error.Action);
        Assert.Equal(OperationPhase.Preparing, error.Phase);
        Assert.NotNull(error.Failure);
        Assert.Empty(_fixture.Installer.GetQueueOperations());
        Assert.Null(_fixture.Installer.CurrentInstall);
    }

    [Fact]
    public async Task WorkerFailureKeepsItsClassificationInsteadOfBecomingUnknown()
    {
        var errors = CaptureErrors();
        _fixture.SetCurrent("base");
        _fixture.Installer.CurrentInstall!.Phase = OperationPhase.Transferring;
        var method = typeof(InstallManager).GetMethod("RequestStoppage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(_fixture.Installer, ["File worker failed", new UnauthorizedAccessException("synthetic-private-path")]);

        await _fixture.Invoke("HandleInstallationStoppage", "File worker failed", null!);

        var error = Assert.Single(errors);
        Assert.True(error.ShowErrorDialog);
        Assert.Equal("permission_denied", error.Failure?.Code);
        Assert.Equal(OperationPhase.Transferring, error.Phase);
    }

    [Fact]
    public async Task ShortcutFailuresAreReportedWithoutFailingTheInstalledGame()
    {
        var errors = CaptureErrors();
        _fixture.SetCurrent("base");
        var item = _fixture.Installer.CurrentInstall!;
        item.Phase = OperationPhase.Finalizing;
        item.CreateDesktopShortcut = true;
        item.CreateStartMenuShortcut = true;
        typeof(InstallManager).GetField("_shortcutManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Installer, new FailingShortcuts());

        await _fixture.Invoke("CreateRequestedShortcutsAsync", item, _fixture.BaseGame, _fixture.BaseGame.LocalAppState!);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.False(error.ShowErrorDialog));
        Assert.NotNull(item.Notice);
        Assert.Null(item.Failure);
        Assert.Contains(errors, error => error.Failure?.Code == "desktop_shortcut_failed");
        Assert.Contains(errors, error => error.Failure?.Code == "start_menu_shortcut_failed");
        Assert.Equal(InstallState.Installed, _fixture.BaseGame.LocalAppState!.InstallStatus);
        Assert.NotEqual(ActionStatus.Failed, item.Status);
    }

    [Fact]
    public async Task RecoveryStateWriteFailureIsReportedAndBlocksUnsafeQueueAdvancement()
    {
        var errors = CaptureErrors();
        _fixture.SetCurrent("base");
        var item = _fixture.Installer.CurrentInstall;
        var file = Path.Combine(_fixture.Root, "data", "install_state.json");
        using var held = File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await _fixture.Invoke("HandleInstallationStoppage", "Synthetic failure", null!);

        Assert.Contains(errors, error => error.Failure?.Code == "operation_state_save_failed" &&
            error.Phase == OperationPhase.Finalizing && error.ShowErrorDialog);
        Assert.Same(item, _fixture.Installer.CurrentInstall);
        _fixture.Installer.AddToQueue(new InstallItem("available", ActionType.Install, _fixture.BasePath));
        Assert.Empty(_fixture.Installer.GetQueueOperations());
        Assert.Equal("operation_state_save_failed", errors[^1].Failure?.Code);
        Assert.True(errors[^1].ShowErrorDialog);
    }

    [Fact]
    public void NotificationBookkeepingIsNotPersistedAsInstallationData()
    {
        var item = new InstallItem("base", ActionType.Install, _fixture.BasePath) { Status = ActionStatus.Failed };
        Publish(item);
        Assert.DoesNotContain("ErrorReported", JsonSerializer.Serialize(item));
    }

    private List<OperationSnapshot> CaptureErrors()
    {
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;
        return errors;
    }

    private void Publish(InstallItem item) => typeof(InstallManager)
        .GetMethod("PublishOperation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_fixture.Installer, [item]);

    private sealed class FailingShortcuts : IGameShortcutManager
    {
        public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) =>
            Task.FromException(new UnauthorizedAccessException("synthetic-private-path"));
        public void Remove(Game game) => throw new IOException("synthetic-private-path");
    }

    public void Dispose() => _fixture.Dispose();
}
