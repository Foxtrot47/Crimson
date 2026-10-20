using System;

namespace Crimson.Models;

public class InstallItem(string appName, ActionType action, string location)
{
    public string AppName { get; set; } = appName;
    public ActionType Action { get; set; } = action;
    public string Location { get; set; } = location;
    public string MoveLocation { get; set; }
    public EpicInstallation? EpicImportSource { get; set; }
    public string? EpicImportOwner { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal int? EpicImportAccountGeneration { get; set; }
    public int ProgressPercentage { get; set; }
    public TimeSpan RunningTime { get; set; }
    public TimeSpan Eta { get; set; }
    public double TotalDownloadSizeMiB { get; set; }
    public double TotalDownloadSizeBytes { get; set; }
    public double DownloadedSizeMiB { get; set; }
    public double TotalWriteSizeBytes { get; set; }
    public double TotalWriteSizeMb { get; set; }
    public double WrittenSizeMiB { get; set; }
    public double DownloadSpeedRawMiB { get; set; }
    public double DownloadSpeedDecompressedMiB { get; set; }
    public double ReadSpeedMiB { get; set; }
    public double WriteSpeedMiB { get; set; }
    public DateTime CreatedTime { get; set; }
    public ActionStatus Status { get; set; } = ActionStatus.Pending;
    public OperationPhase Phase { get; set; } = OperationPhase.Queued;
    public string StatusMessage { get; set; }
    public OperationFailure? Failure { get; set; }
    public OperationFailure? Notice { get; set; }
    internal Guid OperationId { get; } = Guid.NewGuid();
    [System.Text.Json.Serialization.JsonIgnore]
    internal Crimson.Core.TransferRateHistory TransferRates { get; } = new();
    internal bool ErrorReported { get; set; }
    internal int RetryingDownloadCount;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRetrying => System.Threading.Volatile.Read(ref RetryingDownloadCount) > 0;
    public bool CreateDesktopShortcut { get; set; }
    public bool CreateStartMenuShortcut { get; set; }
}

public enum ActionType
{
    Install,
    Import,
    Move,
    Repair,
    Update,
    Uninstall,
    Verify
}

public enum ActionStatus
{
    Pending,
    Success,
    OnGoing,
    Processing,
    Failed,
    Cancelling,
    Cancelled,
    Paused
}
