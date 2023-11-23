using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace Crimson.Core;

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

public class InstallItem
{
    public string AppName { get; set; }
    public ActionType Action { get; set; }
    public string Location { get; set; }
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
    public ActionStatus Status { get; set; }

    public InstallItem(string appName, ActionType action, string location)
    {
        AppName = appName;
        Action = action;
        Status = ActionStatus.Pending;
        Location = location;
    }
}

public static class InstallManager
{
    public static event Action<InstallItem> InstallationStatusChanged;
    public static event Action<InstallItem> InstallProgressUpdate;
    public static InstallItem CurrentInstall;
    private static Queue<InstallItem> _installQueue;
    private static readonly List<InstallItem> InstallHistory = new();

    private static ILogger _log;

    public static void Initialize(string legendaryBinaryPath, ILogger log)
    {
        _installQueue = new Queue<InstallItem>();
        CurrentInstall = null; ;
        _log = log;
    }

    public static void AddToQueue(InstallItem item)
    {
        if (item == null)
            return;

        _log.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        _installQueue.Enqueue(item);
        if (CurrentInstall == null)
            ProcessNext();
    }

    private static void ProcessNext()
    {
        try
        {
            if (CurrentInstall != null || _installQueue.Count <= 0) return;

            CurrentInstall = _installQueue.Dequeue();
            _log.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);
        }
        catch (Exception ex)
        {
            _log.Error("ProcessNext: {Exception}", ex);
            if (CurrentInstall != null)
            {
                CurrentInstall.Status = ActionStatus.Failed;
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }

            CurrentInstall = null;
            ProcessNext();
        }
    }

    public static InstallItem GameGameInQueue(string gameName)
    {
        InstallItem item;
        if (CurrentInstall != null && CurrentInstall.AppName == gameName)
            item = CurrentInstall;
        else
            item = _installQueue.FirstOrDefault(r => r.AppName == gameName);
        return item;
    }

    public static void CancelInstall(string gameName)
    {
    }

    public static List<string> GetQueueItemNames()
    {
        var queueItemsName = new List<string>();
        foreach (var item in _installQueue)
        {
            queueItemsName.Add(item.AppName);
        }
        return queueItemsName;
    }
    public static List<string> GetHistoryItemsNames()
    {
        var historyItemsName = new List<string>();
        foreach (var item in InstallHistory)
        {
            historyItemsName.Add(item.AppName);
        }
        return historyItemsName;
    }
}

internal class InstallItemComparer : IEqualityComparer<InstallItem>
{
    public bool Equals(InstallItem x, InstallItem y)
    {
        return y != null && x != null && x.AppName == y.AppName;
    }

    public int GetHashCode(InstallItem obj)
    {
        return obj.AppName.GetHashCode();
    }
}