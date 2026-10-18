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
        ActionType.Install => "Install",
        ActionType.Update => "Update",
        ActionType.Repair => "Repair",
        ActionType.Verify => "Verify",
        ActionType.Uninstall => "Uninstall",
        ActionType.Move => "Move",
        ActionType.Import => "Import",
        _ => "Operation"
    };

    private static string GetHeadline(OperationSnapshot operation, string label)
    {
        return operation.Status switch
        {
            ActionStatus.Paused => $"{label} paused",
            ActionStatus.Cancelling => "Cancelling",
            ActionStatus.Success => GetCompletedHeadline(operation.Action),
            ActionStatus.Failed => $"{label} failed",
            ActionStatus.Cancelled => $"{label} cancelled",
            _ when IsRetryingTransfer(operation) => "Retrying…",
            _ => GetPhaseHeadline(operation, label)
        };
    }

    private static string GetPhaseHeadline(OperationSnapshot operation, string label) => operation.Phase switch
    {
        OperationPhase.Queued => $"{label} queued",
        OperationPhase.Preparing => "Preparing",
        OperationPhase.Transferring => GetActiveHeadline(operation.Action, label),
        OperationPhase.Verifying => "Verifying",
        OperationPhase.Uninstalling => "Uninstalling",
        OperationPhase.Importing => "Importing",
        OperationPhase.Moving => "Moving",
        OperationPhase.Finalizing => "Finishing",
        _ => label
    };

    private static string GetActiveHeadline(ActionType action, string fallback) => action switch
    {
        ActionType.Install => "Installing",
        ActionType.Update => "Updating",
        ActionType.Repair => "Repairing",
        ActionType.Verify => "Verifying",
        ActionType.Uninstall => "Uninstalling",
        ActionType.Move => "Moving",
        ActionType.Import => "Importing",
        _ => fallback
    };

    private static string GetCompletedHeadline(ActionType action) => action switch
    {
        ActionType.Install => "Installed",
        ActionType.Update => "Updated",
        ActionType.Repair => "Repaired",
        ActionType.Verify => "Verified",
        ActionType.Uninstall => "Uninstalled",
        ActionType.Move => "Moved",
        ActionType.Import => "Imported",
        _ => "Completed"
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
        if (operation.Failure is { } failure)
            return $"{failure.Message} {failure.SuggestedAction}";
        if (IsRetryingTransfer(operation))
            return "Connection interrupted. Retrying…";
        if (!string.IsNullOrWhiteSpace(operation.StatusMessage))
            return operation.StatusMessage;
        if (operation.Phase == OperationPhase.Queued)
            return "Waiting to start";
        if (isIndeterminate || operation.TotalWriteSizeMiB <= 0)
            return string.Empty;

        return $"{StorageSizeFormatter.FormatMebibytes(operation.WrittenSizeMiB)} of " +
            StorageSizeFormatter.FormatMebibytes(operation.TotalWriteSizeMiB);
    }
}
