using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.System;

namespace Crimson.ViewModels;

public partial class AppInstallDialogViewModel : ObservableObject
{
    private readonly InstallManager _installManager;
    private readonly Storage _storageService;
    private readonly ILogger _logger;
    private readonly Windows.System.DispatcherQueue _dispatcherQueue;

    private string _gameAppName;

    [ObservableProperty]
    private string _gameTitle;

    [ObservableProperty]
    private BitmapImage _gameImage;

    [ObservableProperty]
    private string _installLocation;

    [ObservableProperty]
    private bool _isLoadingContent;

    [ObservableProperty]
    private string _baseGameSize;

    [ObservableProperty]
    private string _totalDownloadSize;

    [ObservableProperty]
    private double _totalInstallSizeRaw;

    [ObservableProperty]
    private string _totalInstallSize;

    [ObservableProperty]
    private bool _isDriveSpaceVisible;

    [ObservableProperty]
    private double _driveSpaceUsagePercent;

    [ObservableProperty]
    private string _driveSpaceAvailable;

    [ObservableProperty]
    private string _driveTotalSpace;

    [ObservableProperty]
    private bool _canInstall;

    public event Action RequestClose;
    public event Func<Task<string>> FolderPickerRequested;

    public AppInstallDialogViewModel(
        ILogger logger, InstallManager installManager)
    {
        _logger = logger;
        _installManager = installManager;
        _storageService = new Storage();
        _dispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync(Game gameInfo)
    {
        try
        {
            Activate();
            _gameAppName = gameInfo.AppName;
            GameTitle = gameInfo.AppTitle;
            GameImage = gameInfo.Metadata.KeyImages.FirstOrDefault(i => i.Type == "DieselGameBox") != null ? new BitmapImage(new Uri(gameInfo.Metadata.KeyImages.FirstOrDefault(i => i.Type == "DieselGameBoxTall").Url)) : null;
            InstallLocation = Path.Combine(_storageService.DefaultInstallPath, gameInfo.AppTitle);

            IsLoadingContent = true;
            _ = Task.Run(async () =>
            {
                await LoadGameContent(gameInfo.AppName);
                await UpdateDriveSpace();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize install dialog");
            RequestClose?.Invoke();
        }
    }

    public void Activate()
    {
        IsLoadingContent = true;
        CanInstall = false;
        IsDriveSpaceVisible = false;
        TotalDownloadSize = "0 B";
        TotalInstallSize = "0 B";
        TotalInstallSizeRaw = 0;
        DriveSpaceAvailable = "0 B";
        DriveTotalSpace = "0 B";
        DriveSpaceUsagePercent = 0;
    }

    private async Task LoadGameContent(string appName)
    {
        var (downloadSize, installSize) = await _installManager.GetGameDownloadInstallSizes(appName);
        _dispatcherQueue.TryEnqueue(() =>
        {
            TotalDownloadSize = FormatSize(downloadSize);
            TotalInstallSize = FormatSize(installSize);
            TotalInstallSizeRaw = installSize;
        });
    }

    private async Task UpdateDriveSpace()
    {
        try
        {
            var driveInfo = await _storageService.GetDriveInfo(InstallLocation);
            _dispatcherQueue.TryEnqueue(() =>
            {
                IsDriveSpaceVisible = true;
                var usedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
                DriveSpaceUsagePercent = ((double)usedSpace / driveInfo.TotalSize) * 100;
                DriveSpaceAvailable = FormatSize(driveInfo.AvailableFreeSpace);
                DriveTotalSpace = FormatSize(driveInfo.TotalSize);
                CanInstall = driveInfo.AvailableFreeSpace > TotalInstallSizeRaw;
                IsLoadingContent = false;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get drive space info");
            IsDriveSpaceVisible = false;
        }
    }

    [RelayCommand]
    private async Task SelectLocation()
    {
        if (FolderPickerRequested != null)
        {
            var newPath = await FolderPickerRequested.Invoke();
            if (!string.IsNullOrEmpty(newPath))
            {
                InstallLocation = Path.Combine(newPath, GameTitle);
                await UpdateDriveSpace();
            }
        }
    }

    [RelayCommand]
    private void CloseDialog()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ConfirmInstall()
    {

        RequestClose?.Invoke();
        _installManager.AddToQueue(new InstallItem(_gameAppName, ActionType.Install, InstallLocation));
        _logger.Information("GameInfoViewModel: Added {Game} to Installation Queue", GameTitle);
    }

    private string FormatSize(double bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        var size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}

