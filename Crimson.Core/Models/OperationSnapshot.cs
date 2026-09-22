using System;

namespace Crimson.Models;

public enum OperationPhase
{
    Queued,
    Preparing,
    Transferring,
    Verifying,
    Uninstalling,
    Importing,
    Moving,
    Finalizing
}

public sealed record OperationSnapshot(
    string AppName,
    ActionType Action,
    ActionStatus Status,
    OperationPhase Phase,
    int ProgressPercentage,
    double WrittenSizeMiB,
    double TotalWriteSizeMiB,
    double DownloadSpeedMiB,
    string? StatusMessage)
{
    public bool IsActive => Status is
        ActionStatus.Pending or
        ActionStatus.OnGoing or
        ActionStatus.Processing or
        ActionStatus.Paused or
        ActionStatus.Cancelling;

    public static OperationSnapshot From(InstallItem item) => new(
        item.AppName,
        item.Action,
        item.Status,
        item.Phase,
        Math.Clamp(item.ProgressPercentage, 0, 100),
        item.WrittenSizeMiB,
        item.TotalWriteSizeMb,
        item.DownloadSpeedRawMiB,
        item.StatusMessage);
}
