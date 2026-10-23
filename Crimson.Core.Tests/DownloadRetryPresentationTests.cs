using System.Reflection;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class DownloadRetryPresentationTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Fact]
    public void ConcurrentRetryingChunksPublishOnlyAggregateTransitions()
    {
        _fixture.SetCurrent("base");
        var install = _fixture.Installer.CurrentInstall!;
        install.Phase = OperationPhase.Transferring;
        var updates = new List<OperationSnapshot>();
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationUpdated += updates.Add;
        _fixture.Installer.OperationError += errors.Add;

        ChangeRetry(install, true);
        ChangeRetry(install, true);
        ChangeRetry(install, false);
        Assert.True(install.IsRetrying);
        Assert.True(Assert.Single(updates).IsRetrying);
        ChangeRetry(install, false);

        Assert.False(install.IsRetrying);
        Assert.Equal(new[] { true, false }, updates.Select(update => update.IsRetrying));
        Assert.Empty(errors);
        Assert.False(OperationSnapshot.From(install).IsRetrying);
    }

    [Fact]
    public void OldOperationCallbacksCannotChangeTheNextOperation()
    {
        _fixture.SetCurrent("base");
        var old = _fixture.Installer.CurrentInstall!;
        ChangeRetry(old, true);
        _fixture.SetCurrent("installed");
        ChangeRetry(old, false);

        Assert.False(_fixture.Installer.CurrentInstall!.IsRetrying);
        Assert.Equal(0, _fixture.Installer.CurrentInstall.RetryingDownloadCount);
    }

    [Theory]
    [InlineData(ActionType.Install, "Installing")]
    [InlineData(ActionType.Update, "Updating")]
    [InlineData(ActionType.Repair, "Repairing")]
    public void RetryKeepsActualProgressAndRestoresTheOperationVerbOnRecovery(ActionType action, string recovered)
    {
        var snapshot = Snapshot(action, ActionStatus.Processing) with { IsRetrying = true };
        var presentation = OperationPresenter.Present(snapshot);

        Assert.Equal("Retrying…", presentation.Headline);
        Assert.Equal("Connection interrupted. Retrying…", presentation.Detail);
        Assert.Equal(42, presentation.ProgressPercentage);
        Assert.False(presentation.IsIndeterminate);
        Assert.True(presentation.CanPause);
        Assert.True(presentation.CanCancel);
        Assert.Equal(recovered, OperationPresenter.Present(snapshot with { IsRetrying = false }).Headline);
    }

    [Theory]
    [InlineData(ActionStatus.Paused, "Install paused")]
    [InlineData(ActionStatus.Cancelling, "Cancelling")]
    [InlineData(ActionStatus.Cancelled, "Install cancelled")]
    [InlineData(ActionStatus.Failed, "Install failed")]
    [InlineData(ActionStatus.Success, "Installed")]
    public void LifecycleStatusTakesPrecedenceOverRetry(ActionStatus status, string headline)
    {
        var presentation = OperationPresenter.Present(Snapshot(ActionType.Install, status) with { IsRetrying = true });
        Assert.Equal(headline, presentation.Headline);
        Assert.DoesNotContain("Retrying", presentation.Detail);
    }

    [Fact]
    public void TransientRetryCountsAreNotPersistedOrIncludedInReports()
    {
        _fixture.SetCurrent("base");
        var install = _fixture.Installer.CurrentInstall!;
        ChangeRetry(install, true);
        var json = JsonSerializer.Serialize(install);
        Assert.DoesNotContain("Retrying", json);
        Assert.DoesNotContain("Retrying", OperationDiagnostics.Create(OperationSnapshot.From(install)));
    }

    private static OperationSnapshot Snapshot(ActionType action, ActionStatus status) =>
        new("base", action, status, OperationPhase.Transferring, 42, 42, 100, 10, null);

    private void ChangeRetry(InstallItem item, bool retrying) => typeof(InstallManager)
        .GetMethod("UpdateDownloadRetryState", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_fixture.Installer, [item, retrying]);

    public void Dispose() => _fixture.Dispose();
}
