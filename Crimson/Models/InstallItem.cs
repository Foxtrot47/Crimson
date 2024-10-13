﻿using System;

namespace Crimson.Models;

public class InstallItem(string appName, ActionType action, string location)
{
    public string AppName { get; set; } = appName;
    public ActionType Action { get; set; } = action;
    public string Location { get; set; } = location;
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
}

public enum ActionType
{
    Install,
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
    Cancelled
}
