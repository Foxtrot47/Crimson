using System.Runtime.InteropServices;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Tests;

public sealed class OperationDiagnosticActionsTests
{
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private static OperationSnapshot Failure => new("synthetic", ActionType.Install, ActionStatus.Failed,
        OperationPhase.Transferring, 0, 0, 0, 0, null);

    [Fact]
    public void CopyWritesSanitizedDiagnostics()
    {
        string? text = null;

        var copied = OperationDiagnosticActions.TryCopy(Failure, value => text = value, _log);

        Assert.True(copied);
        Assert.Equal(OperationDiagnostics.Create(Failure), text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipboardBusyDuringSetOrFlushReturnsFailureWithoutThrowing(bool failDuringFlush)
    {
        var contentSet = false;

        var copied = OperationDiagnosticActions.TryCopy(Failure, _ =>
        {
            if (failDuringFlush) contentSet = true;
            throw new COMException("Synthetic clipboard busy", unchecked((int)0x800401D0));
        }, _log);

        Assert.False(copied);
        Assert.Equal(failDuringFlush, contentSet);
    }

    [Fact]
    public void CopyCanBeRetriedAfterAClipboardFailure()
    {
        Assert.False(OperationDiagnosticActions.TryCopy(Failure,
            _ => throw new COMException("Synthetic clipboard busy"), _log));
        string? text = null;

        Assert.True(OperationDiagnosticActions.TryCopy(Failure, value => text = value, _log));
        Assert.NotNull(text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportReturnsTheBrowserLaunchResult(bool launchResult)
    {
        Uri? launched = null;

        var opened = await OperationDiagnosticActions.TryReportAsync(Failure, uri =>
        {
            launched = uri;
            return Task.FromResult(launchResult);
        }, _log);

        Assert.Equal(launchResult, opened);
        Assert.Equal(OperationDiagnostics.CreateIssueUri(Failure), launched);
    }

    [Fact]
    public async Task ReportHandlesSynchronousLaunchExceptions()
    {
        var opened = await OperationDiagnosticActions.TryReportAsync(Failure,
            _ => throw new COMException("Synthetic browser launch failure"), _log);

        Assert.False(opened);
    }

    [Fact]
    public async Task ReportHandlesAsynchronousLaunchExceptionsAndAllowsRetry()
    {
        var failedLaunch = Task.FromException<bool>(new COMException("Synthetic browser launch failure"));
        Assert.False(await OperationDiagnosticActions.TryReportAsync(Failure, _ => failedLaunch, _log));

        Assert.True(await OperationDiagnosticActions.TryReportAsync(Failure, _ => Task.FromResult(true), _log));
    }
}
