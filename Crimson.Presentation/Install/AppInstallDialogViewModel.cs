using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Presentation;

public partial class DlcOptionViewModel : ObservableObject
{
    public DlcOptionViewModel(string appName, string title, bool isSelected = true)
    {
        AppName = appName;
        Title = title;
        _isSelected = isSelected;
    }

    public string AppName { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class AppInstallDialogViewModel : ObservableObject
{
    private readonly IGameWorkflowService _games;
    private readonly ISettingsService _settings;
    private readonly IFolderPickerService _folderPicker;
    private readonly IInstallDialogService _dialog;
    private readonly Dictionary<string, InstallContentSize> _contentSizes = [];
    private string? _appName;
    private long _availableDriveBytes;

    [ObservableProperty]
    private string _gameTitle = string.Empty;

    [ObservableProperty]
    private Uri? _gameImageUri;

    [ObservableProperty]
    private string _installLocation = string.Empty;

    [ObservableProperty]
    private bool _isLoadingContent;

    [ObservableProperty]
    private string _baseGameSize = "0 B";

    [ObservableProperty]
    private string _totalDownloadSize = "0 B";

    [ObservableProperty]
    private double _totalInstallSizeRaw;

    [ObservableProperty]
    private string _totalInstallSize = "0 B";

    [ObservableProperty]
    private bool _isDriveSpaceVisible;

    [ObservableProperty]
    private double _driveSpaceUsagePercent;

    [ObservableProperty]
    private string _driveSpaceAvailable = "0 B";

    [ObservableProperty]
    private string _driveTotalSpace = "0 B";

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private bool _hasDlcs;

    [ObservableProperty]
    private string? _errorMessage;

    public AppInstallDialogViewModel(
        IGameWorkflowService games,
        ISettingsService settings,
        IFolderPickerService folderPicker,
        IInstallDialogService dialog)
    {
        _games = games;
        _settings = settings;
        _folderPicker = folderPicker;
        _dialog = dialog;
    }

    public ObservableCollection<DlcOptionViewModel> AvailableDlcs { get; } = [];

    public async Task LoadAsync(string appName, CancellationToken cancellationToken = default)
    {
        var game = _games.GetGame(appName)
            ?? throw new KeyNotFoundException($"Game is unavailable: {appName}");
        Reset();
        _appName = appName;
        GameTitle = game.Title;
        GameImageUri = game.ImageUri;
        var settings = await _settings.GetAsync(cancellationToken);
        InstallLocation = Path.Combine(settings.DefaultInstallLocation, game.Title);

        foreach (var dlc in _games.GetDlcs(appName))
        {
            var option = new DlcOptionViewModel(dlc.AppName, dlc.Title);
            option.PropertyChanged += OnDlcPropertyChanged;
            AvailableDlcs.Add(option);
        }
        HasDlcs = AvailableDlcs.Count > 0;

        try
        {
            await UpdateDriveSpaceAsync(completeLoading: false, cancellationToken);
            await LoadContentSizesAsync(cancellationToken);
            await UpdateDriveSpaceAsync(completeLoading: true, cancellationToken);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Installation options could not be loaded: {exception.GetType().Name}";
            IsLoadingContent = false;
            CanInstall = false;
        }
    }

    private void Reset()
    {
        foreach (var dlc in AvailableDlcs)
            dlc.PropertyChanged -= OnDlcPropertyChanged;
        AvailableDlcs.Clear();
        _contentSizes.Clear();
        _availableDriveBytes = 0;
        IsLoadingContent = true;
        IsDriveSpaceVisible = false;
        CanInstall = false;
        HasDlcs = false;
        ErrorMessage = null;
        TotalDownloadSize = "0 B";
        TotalInstallSize = "0 B";
        TotalInstallSizeRaw = 0;
        DriveSpaceAvailable = "0 B";
        DriveTotalSpace = "0 B";
        DriveSpaceUsagePercent = 0;
    }

    private async Task LoadContentSizesAsync(CancellationToken cancellationToken)
    {
        _contentSizes[_appName!] = await _games.GetContentSizeAsync(_appName!, cancellationToken);
        foreach (var dlc in AvailableDlcs)
            _contentSizes[dlc.AppName] = await _games.GetContentSizeAsync(dlc.AppName, cancellationToken);
        BaseGameSize = FormatSize(_contentSizes[_appName!].InstallBytes);
        RecalculateSelectedContentSizes();
    }

    private void OnDlcPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DlcOptionViewModel.IsSelected))
            RecalculateSelectedContentSizes();
    }

    private void RecalculateSelectedContentSizes()
    {
        if (_appName is null || !_contentSizes.ContainsKey(_appName))
            return;
        var total = InstallContentSizeCalculator.Calculate(
            _appName,
            _contentSizes,
            AvailableDlcs.Where(item => item.IsSelected).Select(item => item.AppName));
        TotalDownloadSize = FormatSize(total.DownloadBytes);
        TotalInstallSizeRaw = total.InstallBytes;
        TotalInstallSize = FormatSize(total.InstallBytes);
        CanInstall = IsDriveSpaceVisible && _availableDriveBytes > total.InstallBytes;
    }

    private async Task UpdateDriveSpaceAsync(
        bool completeLoading,
        CancellationToken cancellationToken)
    {
        var drive = await _games.GetDriveSpaceAsync(InstallLocation, cancellationToken);
        if (drive is null || drive.TotalBytes <= 0)
        {
            IsDriveSpaceVisible = false;
            CanInstall = false;
            if (completeLoading)
                IsLoadingContent = false;
            return;
        }

        _availableDriveBytes = drive.AvailableBytes;
        IsDriveSpaceVisible = true;
        DriveSpaceUsagePercent = (double)(drive.TotalBytes - drive.AvailableBytes) / drive.TotalBytes * 100;
        DriveSpaceAvailable = FormatSize(drive.AvailableBytes);
        DriveTotalSpace = FormatSize(drive.TotalBytes);
        CanInstall = completeLoading && _availableDriveBytes > TotalInstallSizeRaw;
        if (completeLoading)
            IsLoadingContent = false;
    }

    [RelayCommand]
    private async Task SelectLocationAsync(CancellationToken cancellationToken)
    {
        var selected = await _folderPicker.PickFolderAsync(InstallLocation, cancellationToken);
        if (string.IsNullOrWhiteSpace(selected))
            return;
        InstallLocation = Path.Combine(selected, GameTitle);
        await UpdateDriveSpaceAsync(completeLoading: true, cancellationToken);
    }

    [RelayCommand]
    private void CloseDialog() => _dialog.Close();

    [RelayCommand]
    private async Task ConfirmInstallAsync(CancellationToken cancellationToken)
    {
        if (_appName is null)
            return;
        _dialog.Close();
        await _games.EnqueueAsync(
            new InstallOperationRequest(_appName, ActionType.Install, InstallLocation),
            cancellationToken);
        foreach (var dlc in AvailableDlcs.Where(item => item.IsSelected))
        {
            await _games.EnqueueAsync(
                new InstallOperationRequest(dlc.AppName, ActionType.Install, InstallLocation),
                cancellationToken);
        }
    }

    private static string FormatSize(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }
}
