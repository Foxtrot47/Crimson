using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class TransferRateHistoryTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void RatesUseBytesSincePreviousSampleAndActualElapsedTime()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        history.RecordDownload(8 * MiB);
        history.RecordWrite(4 * MiB);
        time.Advance(2);

        var first = history.Sample()!;
        history.RecordDownload(MiB);
        time.Advance(1);
        var second = history.Sample()!;

        Assert.Equal(4, first.DownloadMiBPerSecond);
        Assert.Equal(2, first.WriteMiBPerSecond);
        Assert.Equal(1, second.DownloadMiBPerSecond);
        Assert.Equal(0, second.WriteMiBPerSecond);
    }

    [Fact]
    public void StalledIntervalsProduceZeroRatherThanStaleRate()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        history.RecordDownload(10 * MiB);
        time.Advance(1);
        Assert.Equal(10, history.Sample()!.DownloadMiBPerSecond);
        time.Advance(1);

        var stalled = history.Sample()!;

        Assert.Equal(0, stalled.DownloadMiBPerSecond);
        Assert.Equal(0, stalled.WriteMiBPerSecond);
    }

    [Fact]
    public void ZeroElapsedDoesNotDivideOrDiscardPendingBytes()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        history.RecordDownload(2 * MiB);
        Assert.Null(history.Sample());
        time.Advance(1);
        Assert.Equal(2, history.Sample()!.DownloadMiBPerSecond);
    }

    [Fact]
    public void DelayedTickUsesActualInterval()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        history.RecordWrite(2 * MiB);
        time.Advance(10);

        Assert.Equal(0.2, history.Sample()!.WriteMiBPerSecond, precision: 8);
    }

    [Fact]
    public void PausedBytesAndWallTimeDoNotCreateResumeSpike()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        history.SetTransferring(false);
        history.RecordDownload(20 * MiB);
        history.RecordWrite(10 * MiB);
        time.Advance(30);
        Assert.Equal(0, history.Sample()!.DownloadMiBPerSecond);
        history.RecordDownload(5 * MiB);
        history.SetTransferring(true);
        history.RecordDownload(MiB);
        history.RecordWrite(2 * MiB);
        time.Advance(1);

        var resumed = history.Sample()!;

        Assert.Equal(1, resumed.DownloadMiBPerSecond);
        Assert.Equal(2, resumed.WriteMiBPerSecond);
    }

    [Fact]
    public void HistoryIsBoundedByBothTimeAndSampleCount()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        for (var second = 1; second <= 90; second++)
        {
            time.Advance(1);
            history.Sample();
        }
        var samples = history.GetSamples();

        Assert.Equal(61, samples.Count);
        Assert.Equal(30, samples[0].Seconds);
        Assert.Equal(90, samples[^1].Seconds);
        time.Advance(61);
        history.Sample();
        Assert.Single(history.GetSamples());
    }

    [Fact]
    public void SnapshotHistoryIsCopiedAndReadOnly()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        time.Advance(1);
        history.Sample();
        var snapshot = history.GetSamples();
        time.Advance(1);
        history.Sample();

        Assert.Single(snapshot);
        Assert.Equal(2, history.GetSamples().Count);
        Assert.Throws<NotSupportedException>(() => ((IList<TransferRateSample>)snapshot).Clear());
    }

    [Fact]
    public void ConcurrentWorkerCountersAreNotLost()
    {
        var time = new ManualTime();
        var history = new TransferRateHistory(time);
        history.SetTransferring(true);
        Parallel.For(0, 1024, _ =>
        {
            history.RecordDownload(1024);
            history.RecordWrite(2048);
        });
        time.Advance(1);

        var sample = history.Sample()!;

        Assert.Equal(1, sample.DownloadMiBPerSecond);
        Assert.Equal(2, sample.WriteMiBPerSecond);
    }

    [Fact]
    public void OperationHistoryIsTransientAndNotIncludedInRecoveryState()
    {
        var install = new InstallItem("synthetic", ActionType.Install, "unused");
        install.TransferRates.RecordDownload(MiB);
        var serialized = JsonSerializer.Serialize(install);

        Assert.DoesNotContain("TransferRates", serialized);
        Assert.DoesNotContain("TransferHistory", serialized);
        Assert.Empty(OperationSnapshot.From(install).TransferHistory!);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
