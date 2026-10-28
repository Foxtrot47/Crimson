using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void AutoDismissesAfterDefaultDuration()
    {
        var clock = new ManualTimeProvider();
        using var service = new NotificationService(clock);
        var changes = new List<AppNotification?>();
        service.Changed += changes.Add;
        var notification = new AppNotification("Added", "A game was added", NotificationSeverity.Success);

        service.Show(notification);

        Assert.Same(notification, service.Current);
        var timer = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromSeconds(6), timer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
        timer.Fire();
        Assert.Null(service.Current);
        Assert.Equal([notification, null], changes);
    }

    [Fact]
    public void OlderTimerCannotDismissReplacement()
    {
        var clock = new ManualTimeProvider();
        using var service = new NotificationService(clock);
        var first = new AppNotification("First", "First message");
        var second = new AppNotification("Second", "Second message");
        service.Show(first);
        var oldTimer = clock.Timers[0];
        service.Show(second, TimeSpan.FromSeconds(10));

        // Timer callbacks may already be queued when a timer is disposed.
        oldTimer.Fire();
        service.Dismiss(first);
        Assert.True(oldTimer.Disposed);
        Assert.Same(second, service.Current);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Timers[1].DueTime);
        clock.Timers[1].Fire();
        Assert.Null(service.Current);
    }

    [Fact]
    public void HoverOrFocusCanPauseAndRestartDismissal()
    {
        var clock = new ManualTimeProvider();
        using var service = new NotificationService(clock);
        var notification = new AppNotification("Added", "Game");
        service.Show(notification);
        service.Pause(notification);
        clock.Timers[0].Fire();
        Assert.Same(notification, service.Current);
        service.Resume(notification);
        Assert.Equal(2, clock.Timers.Count);
        clock.Timers[0].Fire();
        Assert.Same(notification, service.Current);
        clock.Timers[1].Fire();
        Assert.Null(service.Current);
    }

    [Fact]
    public void StaleInteractionCannotPauseReplacement()
    {
        var clock = new ManualTimeProvider();
        using var service = new NotificationService(clock);
        var first = new AppNotification("First", "First");
        var second = new AppNotification("Second", "Second");
        service.Show(first);
        service.Show(second);
        service.Pause(first);
        clock.Timers[1].Fire();
        Assert.Null(service.Current);
    }

    [Fact]
    public void ManualDismissalAndDisposalCancelTimers()
    {
        var clock = new ManualTimeProvider();
        var service = new NotificationService(clock);
        var changes = new List<AppNotification?>();
        service.Changed += changes.Add;
        service.Show(new AppNotification("Added", "Game"));
        service.Dismiss();
        Assert.True(clock.Timers[0].Disposed);
        clock.Timers[0].Fire();
        Assert.Equal(2, changes.Count);
        service.Show(new AppNotification("Added", "Another game"));
        service.Dispose();
        clock.Timers[1].Fire();
        Assert.Null(service.Current);
        Assert.True(clock.Timers[1].Disposed);
        Assert.Throws<ObjectDisposedException>(() => service.Show(new AppNotification("Test", "Message")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveDuration(int seconds)
    {
        using var service = new NotificationService(new ManualTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Show(new AppNotification("Test", "Message"), TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void LibraryMessagesNameSingleGameAndSummarizeBatches()
    {
        var game = new Game { AppName = "game", AppTitle = "Example Game", Metadata = null!, AssetInfos = null! };
        Assert.Null(LibraryNotification.ForAddedGames([]));
        var single = LibraryNotification.ForAddedGames([game]);
        Assert.Equal("New game added", single?.Title);
        Assert.Equal("Example Game is now in your library.", single?.Message);
        Assert.Equal(NotificationSeverity.Success, single?.Severity);
        Assert.Equal("2 games were added to your library.", LibraryNotification.ForAddedGames([game, game])?.Message);
        game.AppTitle = "";
        Assert.Equal("game is now in your library.", LibraryNotification.ForAddedGames([game])?.Message);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool Disposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { DueTime = dueTime; Period = period; return !Disposed; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
