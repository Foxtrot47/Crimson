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
            "Crimson could not write to the selected installation location.",
            "Choose another folder or allow Crimson to write to this location."),
        IOException ioException when IsDiskFull(ioException) => Failure(
            OperationFailureKind.Storage,
            "storage_full",
            "There is not enough free space to complete this operation.",
            "Free some disk space, then retry."),
        HttpRequestException or TimeoutException or TaskCanceledException => Failure(
            OperationFailureKind.Network,
            "network_unavailable",
            "Crimson could not download the required data.",
            "Check your connection and retry."),
        InvalidDataException => Failure(
            OperationFailureKind.Manifest,
            "invalid_install_data",
            "Crimson could not read the installation data.",
            "Retry later. If the problem continues, report it."),
        _ => Failure(
            OperationFailureKind.Unknown,
            "operation_failed",
            "The operation stopped unexpectedly.",
            "Retry the operation. If it fails again, report the problem.")
    };

    private static bool IsDiskFull(IOException exception) =>
        exception.HResult is DiskFullHResult or HandleDiskFullHResult;

    private static OperationFailure Failure(
        OperationFailureKind kind,
        string code,
        string message,
        string suggestedAction) => new(kind, code, message, suggestedAction, true);
}
