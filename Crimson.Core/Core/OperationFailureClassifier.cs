using Crimson.Resources;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Core;

public static class OperationFailureClassifier
{
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    public static OperationFailure Classify(Exception? exception) => exception switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => Classify(aggregate.InnerExceptions[0]),
        UnauthorizedAccessException => Failure(
            OperationFailureKind.Permission,
            "permission_denied",
            UiStrings.CrimsonCouldNotWriteToTheSelectedInstallationLocation,
            UiStrings.ChooseAnotherFolderOrAllowCrimsonToWriteToThisLocation),
        IOException ioException when IsDiskFull(ioException) => Failure(
            OperationFailureKind.Storage,
            "storage_full",
            UiStrings.ThereIsNotEnoughFreeSpaceToCompleteThisOperation,
            UiStrings.FreeSomeDiskSpaceThenRetry),
        HttpRequestException or TimeoutException or TaskCanceledException => Failure(
            OperationFailureKind.Network,
            "network_unavailable",
            UiStrings.CrimsonCouldNotDownloadTheRequiredData,
            UiStrings.CheckYourConnectionAndRetry),
        InvalidDataException => Failure(
            OperationFailureKind.Manifest,
            "invalid_install_data",
            UiStrings.CrimsonCouldNotReadTheInstallationData,
            UiStrings.RetryLaterIfTheProblemContinuesReportIt),
        _ => Failure(
            OperationFailureKind.Unknown,
            "operation_failed",
            UiStrings.TheOperationStoppedUnexpectedly,
            UiStrings.RetryTheOperationIfItFailsAgainReportTheProblem)
    };

    private static bool IsDiskFull(IOException exception) =>
        exception.HResult is DiskFullHResult or HandleDiskFullHResult;

    private static OperationFailure Failure(
        OperationFailureKind kind,
        string code,
        string message,
        string suggestedAction) => new(kind, code, message, suggestedAction, true);
}
