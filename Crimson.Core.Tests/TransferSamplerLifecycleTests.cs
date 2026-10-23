using System.Reflection;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class TransferSamplerLifecycleTests
{
    [Fact]
    public void PauseAndResumeRetainOneSamplerAndClearStaleRates()
    {
        using var fixture = new DlcTestFixture();
        var install = StartSampler(fixture, "base");
        var timer = GetTimer(fixture.Installer);
        install.DownloadSpeedRawMiB = 7;
        install.WriteSpeedMiB = 9;
        install.Status = ActionStatus.Paused;
        Invoke(fixture.Installer, "PublishOperation", install);
        install.TransferRates.RecordDownload(1024);
        Invoke(fixture.Installer, "SampleTransferRates", install);

        Assert.Same(timer, GetTimer(fixture.Installer));
        Assert.Equal(0, install.DownloadSpeedRawMiB);
        Assert.Equal(0, install.WriteSpeedMiB);
        Assert.Equal(0, install.TransferRates.GetSamples()[^1].DownloadMiBPerSecond);
        install.Status = ActionStatus.Processing;
        Invoke(fixture.Installer, "PublishOperation", install);
        Assert.Same(timer, GetTimer(fixture.Installer));
    }

    [Fact]
    public void LateTickCannotPublishOrUpdateSuccessor()
    {
        using var fixture = new DlcTestFixture();
        var old = StartSampler(fixture, "base");
        var oldTimer = GetTimer(fixture.Installer)!;
        var next = StartSampler(fixture, "available");
        var published = new List<OperationSnapshot>();
        fixture.Installer.TransferRatesUpdated += published.Add;
        old.TransferRates.RecordDownload(1024 * 1024);

        Invoke(fixture.Installer, "SampleTransferRates", old);

        Assert.Empty(published);
        Assert.Empty(next.TransferRates.GetSamples());
        Assert.Equal(0, next.DownloadSpeedRawMiB);
        Assert.False(oldTimer.Change(Timeout.Infinite, Timeout.Infinite));
    }

    [Fact]
    public void TerminalOperationStopsSamplerAndIgnoresPendingTick()
    {
        using var fixture = new DlcTestFixture();
        var install = StartSampler(fixture, "base");
        install.Status = ActionStatus.Success;
        Invoke(fixture.Installer, "PublishOperation", install);
        var published = new List<OperationSnapshot>();
        fixture.Installer.TransferRatesUpdated += published.Add;

        Invoke(fixture.Installer, "SampleTransferRates", install);

        Assert.Null(GetTimer(fixture.Installer));
        Assert.Empty(published);
    }

    [Fact]
    public void DisposalDoesNotRestartSamplerOnLatePublication()
    {
        using var fixture = new DlcTestFixture();
        var install = StartSampler(fixture, "base");
        fixture.Installer.Dispose();
        Invoke(fixture.Installer, "PublishOperation", install);

        Assert.Null(GetTimer(fixture.Installer));
    }

    [Fact]
    public void RateSamplesDoNotRebuildOperationListsOrPublishStateChanges()
    {
        using var fixture = new DlcTestFixture();
        var install = StartSampler(fixture, "base");
        using var viewModel = new Crimson.ViewModels.DownloadsViewModel(
            Serilog.Log.Logger, fixture.Installer, fixture.Library, new ImmediateDispatcher());
        fixture.Installer.AddToQueue(new InstallItem("available", ActionType.Install, fixture.BasePath));
        var queue = viewModel.QueueItems;
        var history = viewModel.HistoryItems;
        var stateUpdates = new List<OperationSnapshot>();
        fixture.Installer.OperationUpdated += stateUpdates.Add;
        install.TransferRates.RecordDownload(1024);

        Invoke(fixture.Installer, "SampleTransferRates", install);

        Assert.Empty(stateUpdates);
        Assert.Single(viewModel.TransferHistory);
        Assert.Same(queue, viewModel.QueueItems);
        Assert.Same(history, viewModel.HistoryItems);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }

    private static InstallItem StartSampler(DlcTestFixture fixture, string appName)
    {
        fixture.SetCurrent(appName);
        var install = fixture.Installer.CurrentInstall!;
        install.Phase = OperationPhase.Transferring;
        Invoke(fixture.Installer, "PublishOperation", install);
        GetTimer(fixture.Installer)!.Change(Timeout.Infinite, Timeout.Infinite);
        return install;
    }

    private static Timer? GetTimer(InstallManager installer) => (Timer?)typeof(InstallManager)
        .GetField("_transferTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(installer);

    private static void Invoke(InstallManager installer, string method, InstallItem install) =>
        typeof(InstallManager).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(installer, [install]);
}
