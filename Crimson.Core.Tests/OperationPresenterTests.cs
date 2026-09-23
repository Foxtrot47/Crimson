using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class OperationPresenterTests
{
    [Fact]
    public void QueuedOperationIsActiveAndIndeterminate()
    {
        var presentation = OperationPresenter.Present(Snapshot(
            ActionType.Install,
            ActionStatus.Pending,
            OperationPhase.Queued));

        Assert.True(presentation.IsActive);
        Assert.True(presentation.IsIndeterminate);
        Assert.Equal("Installation queued", presentation.Headline);
        Assert.Equal("Waiting to start", presentation.Detail);
        Assert.False(presentation.CanPause);
        Assert.True(presentation.CanCancel);
    }

    [Theory]
    [InlineData(OperationPhase.Preparing, "Preparing installation")]
    [InlineData(OperationPhase.Verifying, "Verifying files")]
    [InlineData(OperationPhase.Finalizing, "Finishing installation")]
    public void IntermediateInstallPhasesHaveSpecificLabels(OperationPhase phase, string expected)
    {
        var presentation = OperationPresenter.Present(Snapshot(
            ActionType.Install,
            ActionStatus.Pending,
            phase));

        Assert.Equal(expected, presentation.Headline);
        Assert.True(presentation.IsIndeterminate);
    }

    [Fact]
    public void TransferProgressIsDeterminate()
    {
        var presentation = OperationPresenter.Present(new OperationSnapshot(
            "game",
            ActionType.Update,
            ActionStatus.Processing,
            OperationPhase.Transferring,
            42,
            400,
            1000,
            25,
            null));

        Assert.False(presentation.IsIndeterminate);
        Assert.Equal(42, presentation.ProgressPercentage);
        Assert.Equal("Downloading and updating", presentation.Headline);
        Assert.Equal("400.00 MiB of 1000.00 MiB", presentation.Detail);
        Assert.True(presentation.CanPause);
    }

    [Fact]
    public void UninstallUsesRemovalLanguage()
    {
        var presentation = OperationPresenter.Present(Snapshot(
            ActionType.Uninstall,
            ActionStatus.Processing,
            OperationPhase.Uninstalling));

        Assert.Equal("Removing game files", presentation.Headline);
        Assert.False(presentation.IsIndeterminate);
    }

    [Fact]
    public void StructuredFailureHidesRawStatusMessage()
    {
        var presentation = OperationPresenter.Present(new OperationSnapshot(
            "game",
            ActionType.Install,
            ActionStatus.Failed,
            OperationPhase.Preparing,
            0,
            0,
            0,
            0,
            "C:\\Users\\person\\private.file",
            OperationFailureClassifier.Classify(new UnauthorizedAccessException())));

        Assert.DoesNotContain("private.file", presentation.Detail);
        Assert.Contains("selected installation location", presentation.Detail);
        Assert.Contains("Choose another folder", presentation.Detail);
    }

    [Fact]
    public void FailureUsesAvailableStatusMessage()
    {
        var presentation = OperationPresenter.Present(new OperationSnapshot(
            "game",
            ActionType.Repair,
            ActionStatus.Failed,
            OperationPhase.Verifying,
            80,
            0,
            0,
            0,
            "3 files failed verification"));

        Assert.False(presentation.IsActive);
        Assert.Equal("Repair failed", presentation.Headline);
        Assert.Equal("3 files failed verification", presentation.Detail);
    }

    private static OperationSnapshot Snapshot(
        ActionType action,
        ActionStatus status,
        OperationPhase phase) => new(
            "game",
            action,
            status,
            phase,
            0,
            0,
            0,
            0,
            null);
}
