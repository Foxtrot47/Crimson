using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace Crimson.ViewModels;

public partial class AppInstallDialogViewModel : ObservableObject
{
    private readonly InstallManager _installManager;
    private readonly LibraryManager _libraryManager;
    private readonly Storage _storageService;
    private readonly ILogger _logger;
    private readonly Dictionary<string, InstallContentSize> _contentSizes = [];
    private long _availableDriveBytes;

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

    [ObservableProperty]
    private bool _hasDlcs;

    public ObservableCollection<DlcOption> AvailableDlcs { get; } = new();

    public event Action RequestClose;
    public event Func<Task<string>> FolderPickerRequested;

    public AppInstallDialogViewModel(
        ILogger logger,
        InstallManager installManager,
        LibraryManager libraryManager,
        Storage storage)
    {
        _logger = logger;
        _installManager = installManager;
        _libraryManager = libraryManager;
        _storageService = storage;
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

            // Load available DLCs
            AvailableDlcs.Clear();
            var dlcs = _libraryManager.GetDlcsForGame(gameInfo.AppName);
            HasDlcs = dlcs.Count > 0;
            foreach (var dlc in dlcs)
            {
                var option = new DlcOption
                {
                    AppName = dlc.AppName,
                    Title = dlc.AppTitle,
                    IsSelected = true
                };
                option.PropertyChanged += OnDlcPropertyChanged;
                AvailableDlcs.Add(option);
            }

            IsLoadingContent = true;
            await UpdateDriveSpace(completeLoading: false);
            await LoadGameContent(gameInfo.AppName);
            await UpdateDriveSpace();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize install dialog");
            RequestClose?.Invoke();
        }
    }

    public void Activate()
    {
        foreach (var dlc in AvailableDlcs)
            dlc.PropertyChanged -= OnDlcPropertyChanged;
        IsLoadingContent = true;
        CanInstall = false;
        IsDriveSpaceVisible = false;
        HasDlcs = false;
        AvailableDlcs.Clear();
        _contentSizes.Clear();
        _availableDriveBytes = 0;
        TotalDownloadSize = "0 B";
        TotalInstallSize = "0 B";
        TotalInstallSizeRaw = 0;
        DriveSpaceAvailable = "0 B";
        DriveTotalSpace = "0 B";
        DriveSpaceUsagePercent = 0;
    }

    private async Task LoadGameContent(string appName)
    {
        var baseSize = await _installManager.GetGameDownloadInstallSizes(appName);
        _contentSizes[appName] = new InstallContentSize(baseSize.totalDownloadSizeMb, baseSize.totalWriteSizeMb);
        foreach (var dlc in AvailableDlcs)
        {
            var dlcSize = await _installManager.GetGameDownloadInstallSizes(dlc.AppName);
            _contentSizes[dlc.AppName] = new InstallContentSize(
                dlcSize.totalDownloadSizeMb,
                dlcSize.totalWriteSizeMb);
        }

        BaseGameSize = FormatSize(_contentSizes[appName].InstallBytes);
        RecalculateSelectedContentSizes();
    }

    private void OnDlcPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DlcOption.IsSelected))
            RecalculateSelectedContentSizes();
    }

    private void RecalculateSelectedContentSizes()
    {
        var total = InstallContentSizeCalculator.Calculate(
            _gameAppName,
            _contentSizes,
            AvailableDlcs.Where(item => item.IsSelected).Select(item => item.AppName));
        TotalDownloadSize = FormatSize(total.DownloadBytes);
        TotalInstallSizeRaw = total.InstallBytes;
        TotalInstallSize = FormatSize(total.InstallBytes);
        CanInstall = IsDriveSpaceVisible && _availableDriveBytes > total.InstallBytes;
    }

    private async Task UpdateDriveSpace(bool completeLoading = true)
    {
        try
        {
            var driveInfo = await _storageService.GetDriveInfo(InstallLocation);
            IsDriveSpaceVisible = true;
            var usedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
            DriveSpaceUsagePercent = ((double)usedSpace / driveInfo.TotalSize) * 100;
            DriveSpaceAvailable = FormatSize(driveInfo.AvailableFreeSpace);
            DriveTotalSpace = FormatSize(driveInfo.TotalSize);
            _availableDriveBytes = driveInfo.AvailableFreeSpace;
            CanInstall = completeLoading && _availableDriveBytes > TotalInstallSizeRaw;
            if (completeLoading)
                IsLoadingContent = false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get drive space info");
            IsDriveSpaceVisible = false;
            CanInstall = false;
            if (completeLoading)
                IsLoadingContent = false;
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

        // Queue base game install
        _installManager.AddToQueue(new InstallItem(_gameAppName, ActionType.Install, InstallLocation));
        _logger.Information("GameInfoViewModel: Added {Game} to Installation Queue", GameTitle);

        // Queue selected DLCs (install to same base path)
        foreach (var dlc in AvailableDlcs.Where(d => d.IsSelected))
        {
            _installManager.AddToQueue(new InstallItem(dlc.AppName, ActionType.Install, InstallLocation));
            _logger.Information("GameInfoViewModel: Added DLC {Dlc} to Installation Queue", dlc.Title);
        }
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

