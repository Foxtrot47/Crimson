using Crimson.Resources;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.Core;

public sealed record OperationPresentation(
    string Headline,
    string Detail,
    bool IsIndeterminate,
    int ProgressPercentage,
    bool IsActive,
    bool CanPause,
    bool CanResume,
    bool CanCancel);

public static class OperationPresenter
{
    public static OperationPresentation Present(OperationSnapshot operation)
    {
        var label = GetActionLabel(operation.Action);
        var headline = GetHeadline(operation, label);
        var isIndeterminate = IsIndeterminate(operation);

        var canPause = operation.Status == ActionStatus.Processing &&
            operation.Phase is OperationPhase.Transferring or OperationPhase.Uninstalling;
        var canCancel = operation.Status is ActionStatus.Pending or ActionStatus.OnGoing or ActionStatus.Processing or ActionStatus.Paused &&
            operation.Phase is not OperationPhase.Moving and not OperationPhase.Finalizing;

        return new OperationPresentation(
            headline,
            GetDetail(operation, isIndeterminate),
            isIndeterminate,
            operation.ProgressPercentage,
            operation.IsActive,
            canPause,
            operation.Status == ActionStatus.Paused,
            canCancel);
    }

    public static string GetActionLabel(ActionType action) => action switch
    {
        ActionType.Install => UiStrings.Install,
        ActionType.Update => UiStrings.Update,
        ActionType.Repair => UiStrings.Repair,
        ActionType.Verify => UiStrings.Verify,
        ActionType.Uninstall => UiStrings.Uninstall,
        ActionType.Move => UiStrings.Move,
        ActionType.Import => UiStrings.Import,
        _ => UiStrings.Operation
    };

    private static string GetHeadline(OperationSnapshot operation, string label)
    {
        return operation.Status switch
        {
            ActionStatus.Paused => UiStrings.Format(UiStrings.OperationPausedFormat, label),
            ActionStatus.Cancelling => UiStrings.Cancelling,
            ActionStatus.Success => GetCompletedHeadline(operation.Action),
            ActionStatus.Failed => UiStrings.Format(UiStrings.OperationFailedFormat, label),
            ActionStatus.Cancelled => UiStrings.Format(UiStrings.OperationCancelledFormat, label),
            _ when IsRetryingTransfer(operation) => UiStrings.Retrying,
            _ => GetPhaseHeadline(operation, label)
        };
    }

    private static string GetPhaseHeadline(OperationSnapshot operation, string label) => operation.Phase switch
    {
        OperationPhase.Queued => UiStrings.Format(UiStrings.OperationQueuedFormat, label),
        OperationPhase.Preparing => UiStrings.Preparing,
        OperationPhase.Transferring => GetActiveHeadline(operation.Action, label),
        OperationPhase.Verifying => UiStrings.Verifying,
        OperationPhase.Uninstalling => UiStrings.Uninstalling,
        OperationPhase.Importing => UiStrings.Importing,
        OperationPhase.Moving => UiStrings.Moving,
        OperationPhase.Finalizing => UiStrings.Finishing,
        _ => label
    };

    private static string GetActiveHeadline(ActionType action, string fallback) => action switch
    {
        ActionType.Install => UiStrings.Installing,
        ActionType.Update => UiStrings.Updating,
        ActionType.Repair => UiStrings.Repairing,
        ActionType.Verify => UiStrings.Verifying,
        ActionType.Uninstall => UiStrings.Uninstalling,
        ActionType.Move => UiStrings.Moving,
        ActionType.Import => UiStrings.Importing,
        _ => fallback
    };

    private static string GetCompletedHeadline(ActionType action) => action switch
    {
        ActionType.Install => UiStrings.Installed,
        ActionType.Update => UiStrings.Updated,
        ActionType.Repair => UiStrings.Repaired,
        ActionType.Verify => UiStrings.Verified,
        ActionType.Uninstall => UiStrings.Uninstalled,
        ActionType.Move => UiStrings.Moved,
        ActionType.Import => UiStrings.Imported,
        _ => UiStrings.Completed
    };

    private static bool IsIndeterminate(OperationSnapshot operation)
    {
        if (operation.Status is ActionStatus.Paused or ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
            return false;

        return operation.Status == ActionStatus.Cancelling ||
            operation.Phase is OperationPhase.Queued or OperationPhase.Preparing or OperationPhase.Verifying or
                OperationPhase.Importing or OperationPhase.Moving or OperationPhase.Finalizing;
    }

    internal static bool IsRetryingTransfer(OperationSnapshot operation) =>
        operation.IsRetrying && operation.Phase == OperationPhase.Transferring &&
        operation.Status is ActionStatus.Processing or ActionStatus.OnGoing;

    private static string GetDetail(OperationSnapshot operation, bool isIndeterminate)
    {
        if ((operation.Failure ?? operation.Notice) is { } failure)
            return UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
        if (IsRetryingTransfer(operation))
            return UiStrings.ConnectionInterruptedRetrying;
        if (!string.IsNullOrWhiteSpace(operation.StatusMessage))
            return operation.StatusMessage;
        if (operation.Phase == OperationPhase.Queued)
            return UiStrings.WaitingToStart;
        if (isIndeterminate || operation.TotalWriteSizeMiB <= 0)
            return string.Empty;

        return UiStrings.Format(UiStrings.ProgressOfTotalFormat,
            StorageSizeFormatter.FormatMebibytes(operation.WrittenSizeMiB),
            StorageSizeFormatter.FormatMebibytes(operation.TotalWriteSizeMiB));
    }
}
