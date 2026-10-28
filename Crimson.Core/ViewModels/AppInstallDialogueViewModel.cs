using Crimson.Resources;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class AppInstallDialogViewModel : ObservableObject
{
    private readonly InstallManager _installManager;
    private readonly LibraryManager _libraryManager;
    private readonly Storage _storageService;
    private readonly ILogger _logger;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly SettingsManager? _settingsManager;

    private string _gameAppName;
    private InitializationState _initialization = new();

    private sealed class InitializationState(CancellationToken cancellationToken = default)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int DriveRequest;
        public double InstallSize;
        public bool HasInstallSizes;
        public long? AvailableFreeSpace;
        public Dictionary<string, (double DownloadBytes, double WriteBytes)> ContentSizes = new(StringComparer.Ordinal);
    }

    [ObservableProperty]
    private string _gameTitle;

    [ObservableProperty]
    private string? _gameImageUrl;

    [ObservableProperty]
    private string _installLocation;

    [ObservableProperty]
    private bool _isLoadingContent;

    [ObservableProperty]
    private string _baseGameSize;

    [ObservableProperty]
    private string _totalDownloadSize;

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
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    private void ShowLocalError(Exception exception)
    {
        var failure = OperationFailureClassifier.Classify(exception);
        ErrorMessage = UiStrings.Format(UiStrings.MessageSuggestionFormat, failure.Message, failure.SuggestedAction);
    }

    [ObservableProperty]
    private bool _hasDlcs;

    private bool _createDesktopShortcut;
    public bool CreateDesktopShortcut
    {
        get => _createDesktopShortcut;
        set => SetProperty(ref _createDesktopShortcut, value);
    }

    private bool _createStartMenuShortcut;
    public bool CreateStartMenuShortcut
    {
        get => _createStartMenuShortcut;
        set => SetProperty(ref _createStartMenuShortcut, value);
    }

    public ObservableCollection<DlcOption> AvailableDlcs { get; } = new();

    public event Action RequestClose;
    public event Func<Task<string>> FolderPickerRequested;

    public AppInstallDialogViewModel(
        ILogger logger,
        InstallManager installManager,
        LibraryManager libraryManager,
        Storage storage,
        IUiDispatcher uiDispatcher,
        SettingsManager? settingsManager = null)
    {
        _logger = logger;
        _installManager = installManager;
        _libraryManager = libraryManager;
        _storageService = storage;
        _uiDispatcher = uiDispatcher;
        _settingsManager = settingsManager;
    }

    public async Task InitializeAsync(Game gameInfo, CancellationToken cancellationToken = default)
    {
        var generation = new InitializationState(cancellationToken);
        Interlocked.Exchange(ref _initialization, generation);
        try
        {
            Activate();
            _gameAppName = gameInfo.AppName;
            GameTitle = gameInfo.AppTitle;
            GameImageUrl = SelectImageUrl(gameInfo);
            InstallLocation = Path.Combine(
                _settingsManager?.DefaultInstallLocation ?? _storageService.DefaultInstallPath, InstallPathPolicy.GetDirectoryName(gameInfo.AppTitle));

            ClearDlcOptions();
            var dlcs = _libraryManager.GetDlcsForGame(gameInfo.AppName);
            HasDlcs = dlcs.Count > 0;
            foreach (var dlc in dlcs)
            {
                var option = new DlcOption { AppName = dlc.AppName, Title = dlc.AppTitle, IsSelected = true };
                option.PropertyChanged += DlcSelectionChanged;
                AvailableDlcs.Add(option);
            }

            var sizes = await LoadContentSizesAsync(gameInfo.AppName, dlcs, generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentInitialization(generation))
                return;

            generation.ContentSizes = sizes;
            generation.HasInstallSizes = true;
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!IsCurrentInitialization(generation))
                    return;

                BaseGameSize = SizeFormatter.Format(sizes[gameInfo.AppName].WriteBytes);
                UpdateContentTotals(generation);
            });

            await UpdateDriveSpace(InstallLocation, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentInitialization(generation))
                return;

            _logger.Error(ex, "Failed to initialize install dialog");
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!IsCurrentInitialization(generation))
                    return;

                IsLoadingContent = false;
                ShowLocalError(ex);
                _installManager.ReportPreparationFailure(gameInfo.AppName, ActionType.Install, ex);
            });
        }
    }

    private async Task<Dictionary<string, (double DownloadBytes, double WriteBytes)>> LoadContentSizesAsync(
        string baseAppName, IEnumerable<Game> dlcs, InitializationState generation)
    {
        var sizes = new Dictionary<string, (double DownloadBytes, double WriteBytes)>(StringComparer.Ordinal);
        foreach (var appName in dlcs.Select(game => game.AppName).Prepend(baseAppName))
        {
            if (!IsCurrentInitialization(generation)) return sizes;
            sizes[appName] = await _installManager.GetGameDownloadInstallSizes(appName, generation.CancellationToken)
                .ConfigureAwait(false);
        }
        return sizes;
    }

    private void ClearDlcOptions()
    {
        foreach (var option in AvailableDlcs) option.PropertyChanged -= DlcSelectionChanged;
        AvailableDlcs.Clear();
    }

    private void DlcSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DlcOption.IsSelected)) return;
        var generation = Volatile.Read(ref _initialization);
        _uiDispatcher.TryEnqueue(() =>
        {
            if (IsCurrentInitialization(generation)) UpdateContentTotals(generation);
        });
    }

    private void UpdateContentTotals(InitializationState generation)
    {
        if (!generation.HasInstallSizes) { CanInstall = false; return; }
        double download = 0, write = 0;
        foreach (var appName in AvailableDlcs.Where(option => option.IsSelected).Select(option => option.AppName).Prepend(_gameAppName))
        {
            if (!generation.ContentSizes.TryGetValue(appName, out var size)) { CanInstall = false; return; }
            download += size.DownloadBytes;
            write += size.WriteBytes;
        }
        generation.InstallSize = write;
        TotalDownloadSize = SizeFormatter.Format(download);
        TotalInstallSize = SizeFormatter.Format(write);
        CanInstall = generation.AvailableFreeSpace is { } free && free > write;
    }

    internal static string? SelectImageUrl(Game gameInfo)
    {
        var images = gameInfo.Metadata.KeyImages;
        return images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
            ?? images.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
    }

    public void Activate()
    {
        IsLoadingContent = true;
        ErrorMessage = string.Empty;
        CanInstall = false;
        IsDriveSpaceVisible = false;
        HasDlcs = false;
        CreateDesktopShortcut = false;
        CreateStartMenuShortcut = false;
        ClearDlcOptions();
        BaseGameSize = string.Empty;
        TotalDownloadSize = UiStrings.ZeroBytes;
        TotalInstallSize = UiStrings.ZeroBytes;
        DriveSpaceAvailable = UiStrings.ZeroBytes;
        DriveTotalSpace = UiStrings.ZeroBytes;
        DriveSpaceUsagePercent = 0;
        Volatile.Read(ref _initialization).InstallSize = 0;
    }

    private async Task UpdateDriveSpace(string installLocation, InitializationState generation)
    {
        var driveRequest = Interlocked.Increment(ref generation.DriveRequest);
        try
        {
            generation.CancellationToken.ThrowIfCancellationRequested();
            var driveInfo = await _storageService.GetDriveInfo(installLocation).ConfigureAwait(false);
            generation.CancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentRequest(generation, driveRequest))
                return;

            _uiDispatcher.TryEnqueue(() =>
            {
                if (IsCurrentRequest(generation, driveRequest))
                    ApplyDriveSpace(driveInfo, generation);
            });
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(generation, driveRequest))
                return;

            _logger.Warning(ex, "Failed to get drive space info");
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!IsCurrentRequest(generation, driveRequest))
                    return;

                IsDriveSpaceVisible = false;
                CanInstall = false;
                IsLoadingContent = false;
                ShowLocalError(ex);
                _installManager.ReportPreparationFailure(_gameAppName, ActionType.Install, ex);
            });
        }
    }

    private void ApplyDriveSpace(DriveInfo driveInfo, InitializationState generation)
    {
        IsDriveSpaceVisible = true;
        if (generation.HasInstallSizes) ErrorMessage = string.Empty;
        generation.AvailableFreeSpace = driveInfo.AvailableFreeSpace;
        var usedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
        DriveSpaceUsagePercent = ((double)usedSpace / driveInfo.TotalSize) * 100;
        DriveSpaceAvailable = SizeFormatter.Format(driveInfo.AvailableFreeSpace);
        DriveTotalSpace = SizeFormatter.Format(driveInfo.TotalSize);
        UpdateContentTotals(generation);
        IsLoadingContent = false;
    }

    private bool IsCurrentInitialization(InitializationState generation) =>
        ReferenceEquals(generation, Volatile.Read(ref _initialization));

    private bool IsCurrentRequest(InitializationState generation, int driveRequest) =>
        IsCurrentInitialization(generation) &&
        driveRequest == Volatile.Read(ref generation.DriveRequest);

    public void InvalidateInitialization()
    {
        Interlocked.Exchange(ref _initialization, new InitializationState());
        foreach (var option in AvailableDlcs) option.PropertyChanged -= DlcSelectionChanged;
    }

    [RelayCommand]
    private async Task SelectLocation()
    {
        var generation = Volatile.Read(ref _initialization);
        if (FolderPickerRequested is null) return;
        try
        {
            var newPath = await FolderPickerRequested.Invoke();
            if (!string.IsNullOrEmpty(newPath) && IsCurrentInitialization(generation))
            {
                InstallLocation = Path.Combine(newPath, InstallPathPolicy.GetDirectoryName(GameTitle));
                await UpdateDriveSpace(InstallLocation, generation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!IsCurrentInitialization(generation)) return;
            ShowLocalError(exception);
            _installManager.ReportPreparationFailure(_gameAppName, ActionType.Install, exception);
        }
    }

    [RelayCommand]
    private void CloseDialog()
    {
        InvalidateInitialization();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        var install = new InstallItem(_gameAppName, ActionType.Install, InstallLocation)
        {
            CreateDesktopShortcut = CreateDesktopShortcut,
            CreateStartMenuShortcut = CreateStartMenuShortcut
        };
        var selectedDlcs = AvailableDlcs.Where(dlc => dlc.IsSelected).Select(dlc => dlc.AppName).ToList();
        InvalidateInitialization();
        RequestClose?.Invoke();
        if (!_installManager.TryAddToQueue(install)) return;

        foreach (var appName in selectedDlcs)
            _installManager.AddToQueue(new InstallItem(appName, ActionType.Install, install.Location));
        await RememberInstallParentAsync(install);
    }

    private async Task RememberInstallParentAsync(InstallItem install)
    {
        if (_settingsManager is null) return;
        try
        {
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.Location));
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("The installation destination has no parent folder.");
            _settingsManager.DefaultInstallLocation = parent;
            await _settingsManager.SaveSettings();
        }
        catch (Exception exception)
        {
            _logger.Warning("The queued installation's parent folder could not be saved ({ErrorType})", exception.GetType().Name);
            _installManager.ReportSecondaryFailure(install, new OperationFailure(
                OperationFailureKind.Storage, "install_folder_save_failed",
                UiStrings.TheInstallationWasQueuedButCrimsonCouldNotRememberItsFolder,
                UiStrings.ChooseTheFolderAgainNextTimeCheckAppDataPermissionsIfThisContinues, false));
        }
    }
}

