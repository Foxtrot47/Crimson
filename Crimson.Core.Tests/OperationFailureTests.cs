using System.Net.Http;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class OperationFailureTests
{
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), OperationFailureKind.Permission, "permission_denied")]
    [InlineData(typeof(HttpRequestException), OperationFailureKind.Network, "network_unavailable")]
    [InlineData(typeof(InvalidDataException), OperationFailureKind.Manifest, "invalid_install_data")]
    [InlineData(typeof(Exception), OperationFailureKind.Unknown, "operation_failed")]
    public void ClassifierReturnsSafeActionableFailure(
        Type exceptionType,
        OperationFailureKind expectedKind,
        string expectedCode)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var failure = OperationFailureClassifier.Classify(exception);

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(expectedCode, failure.Code);
        Assert.NotEmpty(failure.Message);
        Assert.NotEmpty(failure.SuggestedAction);
        Assert.True(failure.CanRetry);
    }

    [Fact]
    public void DiagnosticsExcludeAppNameStatusMessageAndPaths()
    {
        var snapshot = new OperationSnapshot(
            "private-app-name",
            ActionType.Install,
            ActionStatus.Failed,
            OperationPhase.Preparing,
            0,
            0,
            0,
            0,
            "C:\\Users\\person\\secret.file",
            OperationFailureClassifier.Classify(new UnauthorizedAccessException()));

        var diagnostic = OperationDiagnostics.Create(snapshot);
        var issueUri = OperationDiagnostics.CreateIssueUri(snapshot).ToString();

        Assert.DoesNotContain("private-app-name", diagnostic);
        Assert.DoesNotContain("secret.file", diagnostic);
        Assert.DoesNotContain("private-app-name", issueUri);
        Assert.DoesNotContain("secret.file", issueUri);
        Assert.Contains("permission_denied", diagnostic);
    }
}
