using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;

namespace WinUiApp.Core;

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
    public Process Process { get; set; }

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
    private static readonly Queue<InstallItem> InstallQueue;
    private static readonly List<InstallItem> InstallHistory = new ();

    private static readonly ILogger Log;
    private static readonly string LegendaryBinaryPath;

    static InstallManager()
    {
        InstallQueue = new Queue<InstallItem>();
        CurrentInstall = null;
        var dateTime = DateTime.Now.ToString("yyyy-MM-dd");
        var logFilePath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\logs\Downloader\{dateTime}.txt";
        Log = new LoggerConfiguration().WriteTo.File(logFilePath).CreateLogger();
        LegendaryBinaryPath = $@"C:\Users\{Environment.UserName}\AppData\Local\WinUIEGL\bin\legendary.exe";
        Log.Information("Legendary Binary Path {Path}", LegendaryBinaryPath);
    }

    public static void AddToQueue(InstallItem item)
    {
        if (item == null)
            return;

        Log.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        InstallQueue.Enqueue(item);
        if (CurrentInstall == null)
            ProcessNext();
    }

    private static void ProcessNext()
    {
        try
        {
            if (CurrentInstall != null || InstallQueue.Count <= 0) return;

            CurrentInstall = InstallQueue.Dequeue();
            Log.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);

            CurrentInstall.Process = new Process();
            CurrentInstall.Process.StartInfo.FileName = LegendaryBinaryPath;
            CurrentInstall.Process.StartInfo.Arguments =
                $"{CurrentInstall.Action.ToString().ToLower()} {CurrentInstall.AppName} --base-path {CurrentInstall.Location} --debug -y";
            CurrentInstall.Process.StartInfo.RedirectStandardOutput = true;
            CurrentInstall.Process.StartInfo.RedirectStandardError = true;
            CurrentInstall.Process.StartInfo.RedirectStandardInput = true;
            CurrentInstall.Process.StartInfo.CreateNoWindow = true;

            CurrentInstall.Process.OutputDataReceived += (_, e) => UpdateProgress(e.Data);
            CurrentInstall.Process.ErrorDataReceived += (_, e) => UpdateProgress(e.Data);

            Log.Information(
                "ProcessNext: Starting Legendary Process for {Action} {AppName} with arguments {Argument}",
                CurrentInstall.Action,
                CurrentInstall.AppName,
                CurrentInstall.Process.StartInfo.Arguments
            );

            CurrentInstall.Process.Start();
            CurrentInstall.Process.BeginErrorReadLine();
            CurrentInstall.Process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            Log.Error("ProcessNext: {Exception}", ex);
            if (CurrentInstall != null)
            {
                CurrentInstall.Status = ActionStatus.Failed;
                CurrentInstall.Process.Dispose();
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }

            CurrentInstall = null;
            ProcessNext();
        }
    }

    private static void UpdateProgress(string updateString)
    {
        try
        {
            if (updateString == null || CurrentInstall == null)
                return;

            Log.Information("UpdateProgress: Output received from legendary {Output}", updateString);

            // Update State to Processing just before legendary sends download progress
            if (Regex.Match(updateString,
                @"\[DLManager\] INFO: Starting file writing worker...").Success)
            {
                CurrentInstall.Status = ActionStatus.Processing;
                Log.Information("Started {Action} of {AppName}", CurrentInstall.Action, CurrentInstall.AppName);
                InstallationStatusChanged?.Invoke(CurrentInstall);
                return;
            }

            // Update Progress, StartTime and Eta of Install
            var match = Regex.Match(updateString,
                @"Progress: (\d+)\.\d+%.*Running for (\d{2}:\d{2}:\d{2}).*ETA: (\d{2}:\d{2}:\d{2})");
            if (match.Success)
            {
                CurrentInstall.ProgressPercentage = int.Parse(match.Groups[1].Value);
                CurrentInstall.RunningTime =
                    TimeSpan.ParseExact(match.Groups[2].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture);
                CurrentInstall.Eta =
                    TimeSpan.ParseExact(match.Groups[3].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture);
                Log.Information("UpdateProgress: Progress: {Progress} RunningTime: {RunningTime} Eta: {Eta}",
                    CurrentInstall.ProgressPercentage, CurrentInstall.RunningTime, CurrentInstall.Eta);
                InstallProgressUpdate?.Invoke(CurrentInstall);
                return;
            }

            // Downloaded and Written sizes
            match = Regex.Match(updateString, @"Downloaded: ([\d.]+) MiB, Written: ([\d.]+) MiB");
            if (match.Success)
            {
                CurrentInstall.DownloadedSize = double.Parse(match.Groups[1].Value);
                CurrentInstall.WrittenSize = double.Parse(match.Groups[2].Value);
                InstallProgressUpdate?.Invoke(CurrentInstall);
                return;
            }

            // Extract download speeds (raw and decompressed)
            match = Regex.Match(updateString, @"Download\s+- ([\d.]+) MiB/s \(raw\) / ([\d.]+) MiB/s \(decompressed\)");
            if (match.Success)
            {
                CurrentInstall.DownloadSpeedRaw = double.Parse(match.Groups[1].Value);
                CurrentInstall.DownloadSpeedDecompressed = double.Parse(match.Groups[2].Value);
                InstallProgressUpdate?.Invoke(CurrentInstall);
                return;
            }

            // Extract disk speeds (write and read)
            match = Regex.Match(updateString, @"Disk\s+- ([\d.]+) MiB/s \(write\) / ([\d.]+) MiB/s \(read\)");
            if (match.Success)
            {
                CurrentInstall.WriteSpeed = double.Parse(match.Groups[1].Value);
                CurrentInstall.ReadSpeed = double.Parse(match.Groups[2].Value);
                InstallProgressUpdate?.Invoke(CurrentInstall);
                return;
            }

            // Verification Progress Regex
            match = Regex.Match(updateString, @"Verification progress: (\d+)/\d+ \(\d+\.\d+%\).*\[(\d+\.\d+) MiB/s\]");
            if (match.Success)
            {
                CurrentInstall.Status = ActionStatus.Processing;
                CurrentInstall.ProgressPercentage = int.Parse(match.Groups[1].Value);
                CurrentInstall.ReadSpeed = double.Parse(match.Groups[2].Value);
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }

            // Logic for Installation, Repair, Verify , Update , Move finish are same
            // Installation Finished Regex
            if (Regex.Match(updateString, @"\[cli\] INFO: Finished installation process in \d+.\d+ seconds\.")
                    .Success ||
                Regex.Match(updateString,
                        @"\[cli\] INFO: Download size is 0, the game is either already up to date or has not changed. Exiting..")
                    .Success ||
                Regex.Match(updateString, @"\[cli\] INFO: Game has been uninstalled.").Success ||
                Regex.Match(updateString, @"\[cli\] INFO: Finished.").Success)
            {
                Log.Information("{Action} of {AppName} completed successfully", CurrentInstall.Action,
                    CurrentInstall.AppName);

                CurrentInstall.Status = ActionStatus.Success;
                CurrentInstall.Process.Dispose();
                InstallHistory.Add(CurrentInstall);
                InstallationStatusChanged?.Invoke(CurrentInstall);
                StateManager.FinishedInstall(CurrentInstall);
                CurrentInstall = null;
                ProcessNext();
                return;
            }

            // Cancelling
            if (Regex.Match(updateString, @"\[DLManager\] WARNING: Immediate exit requested!").Success)
            {
                Log.Information("Cancelling {Action} of {AppName}", CurrentInstall.Action, CurrentInstall.AppName);

                CurrentInstall.Status = ActionStatus.Cancelling;
                InstallationStatusChanged?.Invoke(CurrentInstall);
                return;
            }

            // Cancelled
            if (Regex.Match(updateString, @"\[cli\] INFO: Command was aborted via KeyboardInterrupt, cleaning up...")
                .Success || 
                Regex.Match(updateString, @"\[DLManager\] WARNING: Immediate exit requested!")
                .Success
                )
            {
                Log.Information("Cancelled {Action} of {AppName}", CurrentInstall.Action, CurrentInstall.AppName);

                CurrentInstall.Status = ActionStatus.Cancelled;
                InstallHistory.Add(CurrentInstall);
                InstallationStatusChanged?.Invoke(CurrentInstall);
                StateManager.FinishedInstall(CurrentInstall);
                CurrentInstall = null;
                ProcessNext();
                return;
            }

            // Install Size
            match = Regex.Match(updateString, @"\[cli\] INFO: Install size: (\d+.\d+) MiB");
            if (match.Success)
            {
                CurrentInstall.TotalWriteSizeMb = double.Parse(match.Groups[1].Value);
                Log.Information("Install size of {AppName} is {Size} MiB", CurrentInstall.AppName,
                    match.Groups[1].Value);
                InstallationStatusChanged?.Invoke(CurrentInstall);
                return;
            }

            // Download Size
            match = Regex.Match(updateString,
                @"\[cli\] INFO: Download size: (\d+.\d+) MiB \(Compression savings: \d+.\d+%\)");
            if (match.Success)
            {
                CurrentInstall.TotalDownloadSizeMb = double.Parse(match.Groups[1].Value);
                Log.Information("Download size of {AppName} is {Size} MiB", CurrentInstall.AppName,
                    match.Groups[1].Value);
                InstallationStatusChanged?.Invoke(CurrentInstall);
            }
        }
        catch (Exception ex)
        {
            Log.Error("UpdateProgress: {Exception}", ex);
            if (CurrentInstall != null) CurrentInstall.Status = ActionStatus.Failed;
        }
    }

    public static InstallItem GameGameInQueue(string gameName)
    {
        InstallItem item;
        if (CurrentInstall != null && CurrentInstall.AppName == gameName)
            item = CurrentInstall;
        else
            item = InstallQueue.FirstOrDefault(r => r.AppName == gameName);
        return item;
    }

    public static void CancelInstall(string gameName)
    {
        if (CurrentInstall == null || CurrentInstall.AppName != gameName)
            return;
        CurrentInstall.Status = ActionStatus.Cancelling;
        InstallationStatusChanged?.Invoke(CurrentInstall);
        Log.Information("Cancelling {Action} of {AppName}", CurrentInstall.Action, CurrentInstall.AppName);

        // Attach the console of the calling process to the console of the specified process
        AttachConsole((uint)CurrentInstall.Process.Id);

        // Add a ConsoleCtrlDelegate to handle control signals received by the process
        SetConsoleCtrlHandler(ConsoleCtrlCheck, true);

        // Send a Ctrl+C signal to the attached console
        GenerateConsoleCtrlEvent(ConsoleCtrlEvent.CtrlC, 0);
    }

    public static List<string> GetQueueItemNames()
    {
        var queueItemsName = new List<string>();
        foreach (var item in InstallQueue)
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

    // DLL Imports for Windows Kernel
    // Required for sending Ctrl + C to legendary
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(ConsoleCtrlEvent sigevent, int dwProcessGroupId);

    private enum ConsoleCtrlEvent
    {
        CtrlC = 0
    }

    private delegate bool ConsoleCtrlDelegate(CtrlTypes ctrlType);

    // ReSharper disable InconsistentNaming
    private enum CtrlTypes
    {
        CTRL_C_EVENT = 0,
        CTRL_BREAK_EVENT = 1,
        CTRL_CLOSE_EVENT = 2,
        CTRL_LOGOFF_EVENT = 5,
        CTRL_SHUTDOWN_EVENT = 6
    }

    // Ignore Control + Control even created by ourselves
    private static bool ConsoleCtrlCheck(CtrlTypes ctrlType)
    {
        return true;
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