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
using Crimson.Providers;
using Crimson.Providers.Epic;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class AppInstallDialogViewModel : ObservableObject
{
    private readonly LauncherInstaller _installManager;
    private readonly LauncherLibrary _libraryManager;
    private readonly IApplicationStorage _storageService;
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
        public int LanguageRequest;
        public bool HasInstallSizes;
        public long? AvailableFreeSpace;
        public Dictionary<string, (double DownloadBytes, double InstallBytes)> ContentSizes = new(StringComparer.Ordinal);
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

    // Shortcut options are only offered when the owning provider can create them.
    public bool SupportsShortcuts => !string.IsNullOrEmpty(_gameAppName) &&
        _installManager.SupportsShortcutsFor(_gameAppName);

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

    /// <summary>Install languages; empty when the game offers no choice.</summary>
    public InstallLanguageSelection Languages { get; } = new();

    public event Action RequestClose;
    public event Func<Task<string>> FolderPickerRequested;

    public AppInstallDialogViewModel(
        ILogger logger,
        LauncherInstaller installManager,
        LauncherLibrary libraryManager,
        IApplicationStorage storage,
        IUiDispatcher uiDispatcher,
        SettingsManager? settingsManager = null)
    {
        _logger = logger;
        _installManager = installManager;
        _libraryManager = libraryManager;
        _storageService = storage;
        _uiDispatcher = uiDispatcher;
        _settingsManager = settingsManager;
        Languages.SelectionChanged += LanguageSelectionChanged;
    }

    public async Task InitializeAsync(LauncherGame gameInfo, CancellationToken cancellationToken = default)
    {
        var generation = new InitializationState(cancellationToken);
        Interlocked.Exchange(ref _initialization, generation);
        try
        {
            Activate();
            _gameAppName = gameInfo.AppName;
            GameTitle = gameInfo.AppTitle;
            GameImageUrl = SelectImageUrl(gameInfo);
            InstallLocation = Path.Combine(_settingsManager?.DefaultInstallLocation ?? _storageService.DefaultInstallPath,
                SuggestedFolderName(gameInfo.AppTitle));

            var dlcs = LoadDlcOptions(gameInfo);

            var choice = await _installManager.GetInstallLanguages(gameInfo.AppName, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentInitialization(generation))
                return;

            var sizes = await LoadContentSizesAsync(gameInfo.AppName, dlcs,
                choice is null ? null : InstallLanguageSelection.Normalize(choice), generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentInitialization(generation))
                return;

            generation.ContentSizes = sizes;
            generation.HasInstallSizes = true;
            _uiDispatcher.TryEnqueue(() => ApplyLoadedSizes(generation, choice, sizes, gameInfo.AppName));

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

    private List<LauncherGame> LoadDlcOptions(LauncherGame gameInfo)
    {
        ClearDlcOptions();
        // Only Epic installs add-ons as separate jobs; other stores (Ubisoft) gate add-on content by
        // ownership inside the base game's own download, so their add-ons are never offered here.
        var dlcs = gameInfo.Id.ProviderId == EpicLauncherProvider.ProviderId
            ? _libraryManager.GetDlcsForGame(gameInfo.AppName)
            : [];
        HasDlcs = dlcs.Count > 0;
        foreach (var dlc in dlcs)
        {
            var option = new DlcOption { AppName = dlc.AppName, Title = dlc.AppTitle, IsSelected = true };
            option.PropertyChanged += DlcSelectionChanged;
            AvailableDlcs.Add(option);
        }
        return dlcs;
    }

    private void ApplyLoadedSizes(InitializationState generation, InstallLanguageChoice? choice,
        Dictionary<string, (double DownloadBytes, double InstallBytes)> sizes, string baseAppName)
    {
        if (!IsCurrentInitialization(generation))
            return;

        if (choice is not null) Languages.Load(choice);
        BaseGameSize = SizeFormatter.Format(sizes[baseAppName].InstallBytes);
        UpdateContentTotals(generation);
    }

    private async Task<Dictionary<string, (double DownloadBytes, double InstallBytes)>> LoadContentSizesAsync(
        string baseAppName, IEnumerable<LauncherGame> dlcs, IReadOnlyList<string>? languages, InitializationState generation)
    {
        var sizes = new Dictionary<string, (double DownloadBytes, double InstallBytes)>(StringComparer.Ordinal);
        sizes[baseAppName] = languages is null
            ? await _installManager.GetGameDownloadInstallSizes(baseAppName, generation.CancellationToken).ConfigureAwait(false)
            : await _installManager.GetSizesForLanguages(baseAppName, languages, generation.CancellationToken).ConfigureAwait(false);
        foreach (var appName in dlcs.Select(game => game.AppName))
        {
            if (!IsCurrentInitialization(generation)) return sizes;
            sizes[appName] = await _installManager.GetGameDownloadInstallSizes(appName, generation.CancellationToken)
                .ConfigureAwait(false);
        }
        return sizes;
    }

    private async void LanguageSelectionChanged()
    {
        var generation = Volatile.Read(ref _initialization);
        if (!generation.HasInstallSizes || !IsCurrentInitialization(generation)) return;
        var request = Interlocked.Increment(ref generation.LanguageRequest);
        var appName = _gameAppName;
        // The old size no longer applies; Install stays disabled until the new one arrives.
        generation.ContentSizes.Remove(appName);
        UpdateContentTotals(generation);
        var languages = Languages.SelectedCodes;
        if (languages.Count == 0) return;
        try
        {
            var size = await _installManager.GetSizesForLanguages(appName, languages, generation.CancellationToken)
                .ConfigureAwait(false);
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!IsCurrentLanguageRequest(generation, request)) return;
                generation.ContentSizes[appName] = size;
                BaseGameSize = SizeFormatter.Format(size.InstallBytes);
                ErrorMessage = string.Empty;
                UpdateContentTotals(generation);
            });
        }
        catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentLanguageRequest(generation, request)) return;
            _logger.Warning("Install sizes for the selected languages could not be loaded ({ErrorType})", ex.GetType().Name);
            _uiDispatcher.TryEnqueue(() =>
            {
                if (!IsCurrentLanguageRequest(generation, request)) return;
                ShowLocalError(ex);
                _installManager.ReportPreparationFailure(appName, ActionType.Install, ex);
            });
        }
    }

    private bool IsCurrentLanguageRequest(InitializationState generation, int request) =>
        IsCurrentInitialization(generation) && request == Volatile.Read(ref generation.LanguageRequest);

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
        if (!generation.HasInstallSizes || Languages.IsSelectionMissing) { CanInstall = false; return; }
        double download = 0, write = 0;
        foreach (var appName in AvailableDlcs.Where(option => option.IsSelected).Select(option => option.AppName).Prepend(_gameAppName))
        {
            if (!generation.ContentSizes.TryGetValue(appName, out var size)) { CanInstall = false; return; }
            download += size.DownloadBytes;
            write += size.InstallBytes;
        }
        generation.InstallSize = write;
        TotalDownloadSize = SizeFormatter.Format(download);
        TotalInstallSize = SizeFormatter.Format(write);
        CanInstall = generation.AvailableFreeSpace is { } free && free > write;
    }

    internal static string? SelectImageUrl(LauncherGame gameInfo)
    {
        return gameInfo.CoverImageUrl ?? gameInfo.LandscapeImageUrl;
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
        Languages.Clear();
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

    private static string SuggestedFolderName(string title) => InstallPathPolicy.GetDirectoryName(title);

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
                InstallLocation = Path.Combine(newPath, SuggestedFolderName(GameTitle));
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
            CreateDesktopShortcut = SupportsShortcuts && CreateDesktopShortcut,
            CreateStartMenuShortcut = SupportsShortcuts && CreateStartMenuShortcut,
            Languages = Languages.HasOptions ? Languages.SelectedCodes : null
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

