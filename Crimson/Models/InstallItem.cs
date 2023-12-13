using System;

namespace Crimson.Models;

public class InstallItem(string appName, ActionType action, string location)
{
    public string AppName { get; set; } = appName;
    public ActionType Action { get; set; } = action;
    public string Location { get; set; } = location;
    public int ProgressPercentage { get; set; }
    public TimeSpan RunningTime { get; set; }
    public TimeSpan Eta { get; set; }
    public double TotalDownloadSizeMb { get; set; }
    public double DownloadedSize { get; set; }
    public double TotalWriteSizeMb { get; set; }
    public double WrittenSize { get; set; }
    public double DownloadSpeedRaw { get; set; }
    public double DownloadSpeedDecompressed { get; set; }
    public double ReadSpeed { get; set; }
    public double WriteSpeed { get; set; }
    public DateTime CreatedTime { get; set; }
    public ActionStatus Status { get; set; } = ActionStatus.Pending;
}

public enum ActionType
{
    Install,
    Move,
    Repair,
    Update,
    Uninstall
}

public enum ActionStatus
{
    Pending,
    Success,
    OnGoing,
    Processing,
    Failed,
    Cancelling,
    Cancelled
}
