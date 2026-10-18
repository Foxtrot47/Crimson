using Crimson.Models;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class DownloadsViewModelTests
{
    [Fact]
    public void ProgressOnlyUpdateDoesNotRefreshOperationLists()
    {
        var previous = Snapshot(progress: 10, writtenSize: 100, speed: 4);
        var current = Snapshot(progress: 60, writtenSize: 600, speed: 12);

        Assert.False(DownloadsViewModel.HasOperationListChanged([previous], [current]));
    }

    [Fact]
    public void RetryTransitionRefreshesOperationListPresentation()
    {
        var previous = Snapshot();
        var retrying = previous with { IsRetrying = true };
        Assert.True(DownloadsViewModel.HasOperationListChanged([previous], [retrying]));
        Assert.True(DownloadsViewModel.HasOperationListChanged([retrying], [previous]));
    }

    [Fact]
    public void PhaseChangeRefreshesOperationLists()
    {
        var previous = Snapshot(phase: OperationPhase.Queued);
        var current = Snapshot(phase: OperationPhase.Preparing);

        Assert.True(DownloadsViewModel.HasOperationListChanged([previous], [current]));
    }

    [Fact]
    public void MembershipOrOrderChangeRefreshesOperationLists()
    {
        var first = Snapshot(appName: "first");
        var second = Snapshot(appName: "second");

        Assert.True(DownloadsViewModel.HasOperationListChanged([first], [first, second]));
        Assert.True(DownloadsViewModel.HasOperationListChanged([first, second], [second, first]));
    }

    [Fact]
    public void FailureChangeRefreshesOperationLists()
    {
        var previous = Snapshot(status: ActionStatus.Failed);
        var current = previous with
        {
            Failure = new OperationFailure(
                OperationFailureKind.Network,
                "network_error",
                "Download failed.",
                "Retry.",
                true)
        };

        Assert.True(DownloadsViewModel.HasOperationListChanged([previous], [current]));
    }

    private static OperationSnapshot Snapshot(
        string appName = "game",
        ActionStatus status = ActionStatus.Processing,
        OperationPhase phase = OperationPhase.Transferring,
        int progress = 0,
        double writtenSize = 0,
        double speed = 0) => new(
            appName,
            ActionType.Install,
            status,
            phase,
            progress,
            writtenSize,
            1000,
            speed,
            null);
}
