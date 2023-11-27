using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
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

public class InstallManager
{
    public event Action<InstallItem> InstallationStatusChanged;
    public event Action<InstallItem> InstallProgressUpdate;
    public InstallItem CurrentInstall;
    private Queue<InstallItem> _installQueue;
    private readonly List<InstallItem> InstallHistory = new();

    private ILogger _log;
    private readonly LibraryManager _libraryManager;
    private readonly IStoreRepository _repository;
    private readonly Storage _storage;

    public InstallManager(ILogger log, LibraryManager libraryManager, IStoreRepository repository, Storage storage)
    {
        _log = log;
        _libraryManager = libraryManager;
        _installQueue = new Queue<InstallItem>();
        CurrentInstall = null;
        _repository = repository;
        _storage = storage;
    }

    public void AddToQueue(InstallItem item)
    {
        if (item == null)
            return;

        // check if the game is already in the queue
        if (_installQueue.Contains(item, new InstallItemComparer()))
        {
            _log.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
            return;
        }

        // Check if the game we are trying to install exists in the library
        var gameData = _libraryManager.GetGameInfo(item.AppName);
        if (gameData == null)
        {
            _log.Warning("AddToQueue: Game {Name} not found in library", item.AppName);
            return;
        }

        if (item.Action != ActionType.Install && gameData.InstallStatus == InstallState.NotInstalled)
        {
            _log.Warning($"AddToQueue: {item.AppName} is not installed, cannot {item.Action.ToString()}");
            return;
        }

        if (item.Action != ActionType.Repair && gameData.InstallStatus == InstallState.Broken)
        {
            _log.Warning($"AddToQueue: {item.AppName} is broken, forcing repair");
            item.Action = ActionType.Repair;
        }

        if (gameData.IsDlc())
        {
            _log.Warning($"AddToQueue: {item.AppName} is a DLC. DLC Handling is disabled right now");
            return;
        }

        _log.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);
        _installQueue.Enqueue(item);
        if (CurrentInstall == null)
            ProcessNext();
    }

    private async void ProcessNext()
    {
        try
        {
            if (CurrentInstall != null || _installQueue.Count <= 0) return;

            CurrentInstall = _installQueue.Dequeue();
            _log.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
                CurrentInstall.Action, CurrentInstall.AppName, CurrentInstall.Location);

            var gameData = _libraryManager.GetGameInfo(CurrentInstall.AppName);
            var manifestData = await _repository.GetGameManifest(gameData.AssetInfos.Windows.Namespace,
                gameData.AssetInfos.Windows.CatalogItemId, gameData.AppName);

            gameData.BaseUrls = manifestData.BaseUrls;
            _storage.SaveMetaData(gameData);

            _log.Information("ProcessNext: Parsing game manifest");
            var data = Manifest.ReadAll(manifestData.ManifestBytes);

            // TODO Handle stats if game is installed

            // create CurrentInstall.folder if it doesn't exist
            if (!Directory.Exists(CurrentInstall.Location))
            {
                Directory.CreateDirectory(CurrentInstall.Location);
                Console.WriteLine($"Folder created at: {CurrentInstall.Location}");
            }

            if (!HasFolderWritePermissions(CurrentInstall.Location))
            {
                _log.Error("ProcessNext: No write permissions to {Location}", CurrentInstall.Location);
                CurrentInstall.Status = ActionStatus.Failed;
                InstallationStatusChanged?.Invoke(CurrentInstall);
                CurrentInstall = null;
                ProcessNext();
            }
            
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

    public InstallItem GameGameInQueue(string gameName)
    {
        InstallItem item;
        if (CurrentInstall != null && CurrentInstall.AppName == gameName)
            item = CurrentInstall;
        else
            item = _installQueue.FirstOrDefault(r => r.AppName == gameName);
        return item;
    }

    public void CancelInstall(string gameName)
    {
    }

    public List<string> GetQueueItemNames()
    {
        var queueItemsName = new List<string>();
        foreach (var item in _installQueue)
        {
            queueItemsName.Add(item.AppName);
        }

        return queueItemsName;
    }

    public List<string> GetHistoryItemsNames()
    {
        var historyItemsName = new List<string>();
        foreach (var item in InstallHistory)
        {
            historyItemsName.Add(item.AppName);
        }

        return historyItemsName;
    }

    private bool HasFolderWritePermissions(string folderPath)
    {
        try
        {
            // Create a DirectoryInfo object representing the specified directory.
            var directoryInfo = new DirectoryInfo(folderPath);

            // Get the access control list for the folder
            var directorySecurity = directoryInfo.GetAccessControl();

            // Get the access rules for the current user
            var accessRules = directorySecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

            // Check if the current user has write permissions
            var currentUser = WindowsIdentity.GetCurrent();
            var hasWritePermissions = accessRules.Cast<FileSystemAccessRule>().Any(rule =>
                currentUser.User.Equals(rule.IdentityReference) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.Write) == FileSystemRights.Write);

            return hasWritePermissions;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"An error occurred: {ex.Message}");
            return false;
        }
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