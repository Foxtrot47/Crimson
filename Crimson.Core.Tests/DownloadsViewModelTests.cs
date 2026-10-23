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

    [Fact]
    public void TelemetryOnlyUpdateDoesNotRefreshQueueOrHistory()
    {
        var previous = Snapshot();
        var current = previous with
        {
            DownloadSpeedMiB = 12, WriteSpeedMiB = 30,
            TransferHistory = [new TransferRateSample(1, 12, 30)]
        };

        Assert.False(DownloadsViewModel.HasOperationListChanged([previous], [current]));
    }

    [Theory]
    [InlineData(ActionStatus.Processing, OperationPhase.Transferring, "5 MiB/s", "8 MiB/s")]
    [InlineData(ActionStatus.OnGoing, OperationPhase.Transferring, "5 MiB/s", "8 MiB/s")]
    [InlineData(ActionStatus.Paused, OperationPhase.Transferring, "0 MiB/s", "0 MiB/s")]
    [InlineData(ActionStatus.Cancelling, OperationPhase.Transferring, "0 MiB/s", "0 MiB/s")]
    [InlineData(ActionStatus.Processing, OperationPhase.Verifying, "0 MiB/s", "0 MiB/s")]
    [InlineData(ActionStatus.OnGoing, OperationPhase.Preparing, "0 MiB/s", "0 MiB/s")]
    public void SummaryRatesFollowLifecycle(ActionStatus status, OperationPhase phase, string download, string diskWrite)
    {
        using var fixture = new DlcTestFixture();
        using var viewModel = CreateViewModel(fixture);
        var operation = Snapshot(status: status, phase: phase, speed: 5) with
        {
            WriteSpeedMiB = 8, TransferHistory = [new TransferRateSample(1, 5, 8)]
        };

        Apply(viewModel, operation);

        Assert.Equal(download, viewModel.CurrentDownloadSpeed);
        Assert.Equal(diskWrite, viewModel.CurrentDiskWriteSpeed);
        Assert.Same(operation.TransferHistory, viewModel.TransferHistory);
    }

    [Fact]
    public void RetryCopyAndNewOperationHistoryArePreservedCorrectly()
    {
        using var fixture = new DlcTestFixture();
        using var viewModel = CreateViewModel(fixture);
        Apply(viewModel, Snapshot(speed: 5) with
        {
            IsRetrying = true, WriteSpeedMiB = 3, TransferHistory = [new TransferRateSample(1, 5, 3)]
        });
        Assert.Equal("Retrying…", viewModel.CurrentDownloadSpeed);
        Assert.Equal("3 MiB/s", viewModel.CurrentDiskWriteSpeed);
        Assert.Equal("Retrying…", viewModel.CurrentInstallAction);

        Apply(viewModel, Snapshot(appName: "next", phase: OperationPhase.Preparing));

        Assert.Empty(viewModel.TransferHistory);
        Assert.Equal("0 MiB/s", viewModel.CurrentDownloadSpeed);
    }

    [Theory]
    [InlineData(ActionType.Install, OperationPhase.Transferring, "Installing", true, true)]
    [InlineData(ActionType.Update, OperationPhase.Transferring, "Updating", true, true)]
    [InlineData(ActionType.Repair, OperationPhase.Transferring, "Repairing", true, true)]
    [InlineData(ActionType.Verify, OperationPhase.Verifying, "Verifying", false, true)]
    [InlineData(ActionType.Import, OperationPhase.Importing, "Importing", false, true)]
    [InlineData(ActionType.Move, OperationPhase.Moving, "Moving", false, false)]
    [InlineData(ActionType.Uninstall, OperationPhase.Uninstalling, "Uninstalling", true, true)]
    public void AllActionsUseSharedPresentationWithAccurateControls(
        ActionType action, OperationPhase phase, string headline, bool canPause, bool canCancel)
    {
        using var fixture = new DlcTestFixture();
        using var viewModel = CreateViewModel(fixture);
        Apply(viewModel, Snapshot(phase: phase) with { Action = action });

        Assert.Equal(headline, viewModel.CurrentInstallAction);
        Assert.Equal("0 MiB/s", viewModel.CurrentDownloadSpeed);
        Assert.Equal("0 MiB/s", viewModel.CurrentDiskWriteSpeed);
        Assert.Empty(viewModel.TransferHistory);
        Assert.Equal(canPause, viewModel.EnablePauseButton);
        Assert.Equal(canCancel, viewModel.EnableCancelButton);
        Assert.True(viewModel.ShowPauseButton);
        Assert.False(viewModel.ShowResumeButton);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidOrZeroRatesHaveFiniteZeroPresentation(double rate) =>
        Assert.Equal("0 MiB/s", DownloadsViewModel.FormatTransferRate(rate));

    [Fact]
    public void RateSummariesShowCurrentRatesRatherThanHistoricalPeaks()
    {
        using var fixture = new DlcTestFixture();
        using var viewModel = CreateViewModel(fixture);
        Apply(viewModel, Snapshot(speed: 3) with
        {
            WriteSpeedMiB = 2,
            TransferHistory = [new TransferRateSample(1, 10, 8), new TransferRateSample(2, 3, 2)]
        });
        Assert.Equal("3 MiB/s", viewModel.CurrentDownloadSpeed);
        Assert.Equal("2 MiB/s", viewModel.CurrentDiskWriteSpeed);
        Assert.Equal(2, viewModel.TransferHistory.Count);

        Apply(viewModel, Snapshot(phase: OperationPhase.Preparing));
        Assert.Equal("0 MiB/s", viewModel.CurrentDownloadSpeed);
        Assert.Equal("0 MiB/s", viewModel.CurrentDiskWriteSpeed);
        Assert.Empty(viewModel.TransferHistory);
    }

    [Fact]
    public void BannerArtworkDoesNotUsePortraitOrKnownSquareImages()
    {
        using var fixture = new DlcTestFixture();
        fixture.BaseGame.Metadata.KeyImages =
        [
            new KeyImage { Type = "DieselGameBoxTall", Width = 600, Height = 900, Url = "https://cdn1.epicgames.com/portrait.png" },
            new KeyImage { Type = "DieselGameBox", Width = 512, Height = 512, Url = "https://cdn1.epicgames.com/square.png" }
        ];
        Assert.Null(DownloadsViewModel.FindBannerUrl(fixture.BaseGame));
        fixture.BaseGame.Metadata.KeyImages.Add(new KeyImage
        {
            Type = "OfferImageWide", Width = 1920, Height = 1080, Url = "https://cdn1.epicgames.com/wide.png"
        });
        Assert.Equal("https://cdn1.epicgames.com/wide.png", DownloadsViewModel.FindBannerUrl(fixture.BaseGame));
    }

    [Theory]
    [InlineData("http://cdn1.epicgames.com/wide.png")]
    [InlineData("https://unapproved.example/wide.png")]
    public void BannerArtworkRejectsUnapprovedSources(string url)
    {
        using var fixture = new DlcTestFixture();
        fixture.BaseGame.Metadata.KeyImages =
            [new KeyImage { Type = "OfferImageWide", Width = 1920, Height = 1080, Url = url }];
        Assert.Null(DownloadsViewModel.FindBannerUrl(fixture.BaseGame));
    }

    [Fact]
    public void SlowTransfersDoNotRoundToZeroMebibytes()
    {
        Assert.Equal("512 KiB/s", DownloadsViewModel.FormatTransferRate(0.5));
        Assert.Equal("512 B/s", DownloadsViewModel.FormatTransferRate(1d / 2048));
    }

    private static DownloadsViewModel CreateViewModel(DlcTestFixture fixture) => new(
        Serilog.Log.Logger, fixture.Installer, fixture.Library, new ImmediateDispatcher());

    private static void Apply(DownloadsViewModel viewModel, OperationSnapshot operation) =>
        typeof(DownloadsViewModel).GetMethod("ApplyOperationPresentation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(viewModel, [operation]);

    [Fact]
    public void QueuedRateUpdateCannotRestoreAnObsoleteTransferPhase()
    {
        using var fixture = new DlcTestFixture();
        fixture.SetCurrent("base");
        var install = fixture.Installer.CurrentInstall!;
        install.Phase = OperationPhase.Transferring;
        var dispatcher = new QueuedDispatcher();
        using var viewModel = new DownloadsViewModel(Serilog.Log.Logger, fixture.Installer, fixture.Library, dispatcher);
        var snapshot = OperationSnapshot.From(install) with { DownloadSpeedMiB = 8, WriteSpeedMiB = 12 };
        typeof(DownloadsViewModel).GetMethod("HandleTransferRatesUpdated",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(viewModel, [snapshot]);
        install.Phase = OperationPhase.Verifying;

        dispatcher.Flush();

        Assert.Equal("Verifying", viewModel.CurrentInstallAction);
        Assert.Equal("0 MiB/s", viewModel.CurrentDownloadSpeed);
        Assert.Equal("0 MiB/s", viewModel.CurrentDiskWriteSpeed);
    }

    private sealed class QueuedDispatcher : Crimson.Core.IUiDispatcher
    {
        private readonly Queue<Action> _callbacks = new();
        public bool TryEnqueue(Action callback) { _callbacks.Enqueue(callback); return true; }
        public void Flush() { while (_callbacks.TryDequeue(out var callback)) callback(); }
    }

    private sealed class ImmediateDispatcher : Crimson.Core.IUiDispatcher
    {
        public bool TryEnqueue(Action callback) { callback(); return true; }
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
