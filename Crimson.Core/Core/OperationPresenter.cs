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
        ActionType.Install => "Installation",
        ActionType.Update => "Update",
        ActionType.Repair => "Repair",
        ActionType.Verify => "Verification",
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
            ActionStatus.Cancelling => $"Cancelling {label.ToLowerInvariant()}",
            ActionStatus.Success => $"{label} completed",
            ActionStatus.Failed => $"{label} failed",
            ActionStatus.Cancelled => $"{label} cancelled",
            _ => GetPhaseHeadline(operation, label)
        };
    }

    private static string GetPhaseHeadline(OperationSnapshot operation, string label) => operation.Phase switch
    {
        OperationPhase.Queued => $"{label} queued",
        OperationPhase.Preparing => $"Preparing {label.ToLowerInvariant()}",
        OperationPhase.Transferring => GetTransferHeadline(operation.Action, label),
        OperationPhase.Verifying => "Verifying files",
        OperationPhase.Uninstalling => "Removing game files",
        OperationPhase.Importing => "Importing game",
        OperationPhase.Moving => "Moving game",
        OperationPhase.Finalizing => $"Finishing {label.ToLowerInvariant()}",
        _ => label
    };

    private static string GetTransferHeadline(ActionType action, string fallback) => action switch
    {
        ActionType.Install => "Downloading and installing",
        ActionType.Update => "Downloading and updating",
        ActionType.Repair or ActionType.Verify => "Repairing files",
        _ => fallback
    };

    private static bool IsIndeterminate(OperationSnapshot operation)
    {
        if (operation.Status is ActionStatus.Paused or ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
            return false;

        return operation.Status == ActionStatus.Cancelling ||
            operation.Phase is OperationPhase.Queued or OperationPhase.Preparing or OperationPhase.Verifying or
                OperationPhase.Importing or OperationPhase.Moving or OperationPhase.Finalizing;
    }

    private static string GetDetail(OperationSnapshot operation, bool isIndeterminate)
    {
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
