using Crimson.Core;

namespace Crimson.Tests;

public sealed class DialogCoordinatorTests
{
    [Fact]
    public async Task DialogsAreShownSequentiallyWithoutLosingQueuedRequests()
    {
        var coordinator = new DialogCoordinator();
        var closeFirst = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var first = coordinator.ShowAsync(() => { order.Add(1); return closeFirst.Task; });
        var second = coordinator.ShowAsync(() => { order.Add(2); return Task.FromResult(2); });
        Assert.Equal([1], order);

        closeFirst.SetResult(1);
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task CancellingQueuedDialogDoesNotReleaseTheActiveDialogsSlot()
    {
        var coordinator = new DialogCoordinator();
        var closeFirst = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ShowAsync(() => closeFirst.Task);
        using var cancellation = new CancellationTokenSource();
        var cancelledShown = false;
        var cancelled = coordinator.ShowAsync(() => { cancelledShown = true; return Task.FromResult(2); }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var thirdShown = false;
        var third = coordinator.ShowAsync(() => { thirdShown = true; return Task.FromResult(3); });
        Assert.False(cancelledShown);
        Assert.False(thirdShown);

        closeFirst.SetResult(1);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, await third.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FailedDialogDoesNotBlockTheNextErrorDialog()
    {
        var coordinator = new DialogCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ShowAsync(() =>
            Task.FromException<int>(new InvalidOperationException("Synthetic dialog failure"))));

        Assert.Equal(2, await coordinator.ShowAsync(() => Task.FromResult(2)).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellingAnActiveScopeDoesNotOverlapItsClosingDialog()
    {
        var coordinator = new DialogCoordinator();
        using var cancellation = new CancellationTokenSource();
        var closeFirst = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ShowAsync(() => closeFirst.Task, cancellation.Token);
        cancellation.Cancel();
        var secondShown = false;
        var second = coordinator.ShowAsync(() => { secondShown = true; return Task.FromResult(2); });
        Assert.False(secondShown);

        closeFirst.SetResult(1);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
