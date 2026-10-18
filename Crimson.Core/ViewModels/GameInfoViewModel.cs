using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.ViewModels;

/// <summary>
/// Page for Showing Details of individual game and allowing play
/// download and other options
/// </summary>
public partial class GameInfoViewModel : ObservableObject, INavigationAware
{
    private readonly IUiDispatcher _uiDispatcher;
    private readonly InstallManager _installer;
    private readonly LibraryManager _libraryManager;
    private readonly Storage _storage;
    private readonly ILogger<GameInfoViewModel> _log;
    private CancellationTokenSource? _storeDetailsCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorePageUri), nameof(CanViewStore))]
    private Game? _game;

    public Uri? StorePageUri => EpicStoreLinks.ForGame(Game);
    public bool CanViewStore => StorePageUri is not null;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isManageEnabled;

    [ObservableProperty]
    private bool _isImportVisible;

    [ObservableProperty]
    private bool _isImportEnabled;

    [ObservableProperty]
    private string _primaryActionButtonText;

    [ObservableProperty]
    private GamePrimaryAction _primaryAction;

    [ObservableProperty]
    private bool _isPrimaryActionEnabled = true;

    [ObservableProperty]
    private bool _isProgressRingVisible;

    [ObservableProperty]
    private bool _isProgressRingIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string? _coverImageUrl;

    [ObservableProperty]
    private bool _hasCoverImage;

    [ObservableProperty]
    private string? _heroImageUrl;

    [ObservableProperty]
    private bool _hasHeroImage;

    [ObservableProperty]
    private IReadOnlyList<StoreProductImage> _screenshots = [];

    [ObservableProperty]
    private bool _hasScreenshots;

    [ObservableProperty]
    private string _developerName = string.Empty;

    [ObservableProperty]
    private string _publisherName = string.Empty;

    [ObservableProperty]
    private string _gameDescription = string.Empty;

    [ObservableProperty]
    private bool _hasGameDescription;

    [ObservableProperty]
    private bool _isDetailsLoading;

    [ObservableProperty]
    private bool _isStoreDetailsUnavailable;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private bool _hasTags;

    [ObservableProperty]
    private string _releaseDateText = "Unavailable";

    [ObservableProperty]
    private string _lastUpdatedText = "Unavailable";

    [ObservableProperty]
    private string _totalPlayTimeText = "Unavailable";

    [ObservableProperty]
    private IReadOnlyList<StoreSystemRequirement> _windowsRequirements = [];

    [ObservableProperty]
    private bool _hasWindowsRequirements;

    [ObservableProperty]
    private string _gameStatusText = string.Empty;

    [ObservableProperty]
    private string _installedVersion = "Unavailable";

    // Event for showing install dialog
    public event Func<Task> ShowInstallDialogRequested;
    public event Action CloseInstallDialogRequested;

    // Event for requesting folder picker from view
    public event Func<Task<string>> FolderPickerRequested;

    public GameInfoViewModel(ILogger<GameInfoViewModel> logger,
            InstallManager installer,
            LibraryManager libraryManager,
            Storage storage,
            IUiDispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _log = logger;
        _installer = installer;
        _libraryManager = libraryManager;
        _storage = storage;

    }

    public async Task OnNavigatedTo(object parameter)
    {
        if (parameter is not string appName) return;

        UnregisterEventHandlers();
        CancelStoreDetailsRequest();
        Game = _libraryManager.GetGameInfo(appName);
        if (Game is null) return;

        ApplyCatalogMetadata(Game);
        _libraryManager.GameStatusUpdated += CheckGameStatus;
        _installer.OperationUpdated += HandleOperationUpdated;
        CheckGameStatus(Game);

        _storeDetailsCancellation = new CancellationTokenSource();
        await Task.WhenAll(
            LoadStoreDetails(appName, Game.Metadata.Namespace, _storeDetailsCancellation.Token),
            LoadPlaytime(appName, _storeDetailsCancellation.Token));
    }

    private void UnregisterEventHandlers()
    {
        _libraryManager.GameStatusUpdated -= CheckGameStatus;
        _installer.OperationUpdated -= HandleOperationUpdated;
    }

    private void CancelStoreDetailsRequest()
    {
        _storeDetailsCancellation?.Cancel();
        _storeDetailsCancellation?.Dispose();
        _storeDetailsCancellation = null;
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        var requestedAction = GetPrimaryOperationAction();
        try
        {
            if (!CanRunPrimaryAction()) return;
            _log.LogInformation("GameInfoPage: Primary Action Button Clicked for {Game}", Game.AppTitle);

            if (Game.LocalAppState?.InstallStatus == InstallState.Installed)
            {
                _log.LogInformation("GameInfoPage: Starting Game {Game}", Game.AppTitle);
                await _libraryManager.LaunchApp(Game.AppName);
                return;
            }

            if (Game.LocalAppState?.InstallStatus == InstallState.NeedUpdate)
            {
                _log.LogInformation("GameInfoPage: Updating Game {Game}", Game.AppTitle);
                _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Update, Game.LocalAppState.InstallPath));
                return;
            }

            if (Game.LocalAppState?.InstallStatus == InstallState.Broken)
            {
                _log.LogInformation("GameInfoPage: Repairing Game {Game}", Game.AppTitle);
                _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Repair, Game.LocalAppState.InstallPath));
                return;
            }

            if (ShowInstallDialogRequested != null)
            {
                await ShowInstallDialogRequested.Invoke();
            }
            else
            {
                _installer.ReportPreparationFailure(Game.AppName, ActionType.Install, new OperationFailure(
                    OperationFailureKind.Unknown, "install_dialog_unavailable", "The installation dialog could not be opened.",
                    "Restart Crimson and try again.", false));
            }
        }
        catch (Exception ex)
        {
            _log.LogError("PrimaryActionAsync failed: {ErrorType}", ex.GetType().Name);
            ReportPrimaryActionFailure(requestedAction, ex);
            RefreshActionState();
        }
    }

    private void ReportPrimaryActionFailure(ActionType action, Exception exception) =>
        _installer.ReportPreparationFailure(Game?.AppName ?? string.Empty, action, exception);

    private ActionType GetPrimaryOperationAction() => PrimaryAction switch
    {
        GamePrimaryAction.Update => ActionType.Update,
        GamePrimaryAction.Repair => ActionType.Repair,
        _ => ActionType.Install
    };

    private void RefreshActionState()
    {
        if (Game is null) return;
        ApplyGameStatus(Game.LocalAppState?.InstallStatus);
        ApplyOperation(FindOperation(Game.AppName));
    }

    private void HandleOperationUpdated(OperationSnapshot operation)
    {
        try
        {
            if (Game is null || !_libraryManager.IsOperationForGame(operation.AppName, Game.AppName))
                return;
            var appName = Game.AppName;
            _uiDispatcher.TryEnqueue(() =>
            {
                if (Game?.AppName != appName) return;
                ApplyOperation(FindOperation(appName));
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HandleOperationUpdated failed");
        }
    }

    private OperationSnapshot? FindOperation(string appName) =>
        _installer.GetActiveOperationForGame(appName);

    private void ApplyOperation(OperationSnapshot? operation)
    {
        if (operation is null)
        {
            if (Game is not null)
                ApplyGameStatus(Game.LocalAppState?.InstallStatus);
            return;
        }

        var presentation = OperationPresenter.Present(operation);
        IsProgressRingVisible = presentation.IsActive;
        IsProgressRingIndeterminate = presentation.IsIndeterminate;
        ProgressValue = presentation.ProgressPercentage;
        IsPrimaryActionEnabled = !presentation.IsActive;
        IsManageEnabled = IsInstalled && !presentation.IsActive;
        IsImportEnabled = IsImportVisible && !presentation.IsActive;

        if (presentation.IsActive)
        {
            PrimaryAction = GamePrimaryAction.None;
            PrimaryActionButtonText = presentation.Headline;
            GameStatusText = presentation.Headline;
        }
        else if (Game is not null)
            ApplyGameStatus(Game.LocalAppState?.InstallStatus);
    }

    private void CheckGameStatus(Game updatedGame)
    {
        if (updatedGame == null || Game is null || updatedGame.AppName != Game.AppName) return;
        _log.LogInformation("GameInfoPage: Game Status Changed for {Game}", updatedGame.AppTitle);
        _uiDispatcher.TryEnqueue(() =>
        {
            if (Game?.AppName != updatedGame.AppName) return;
            Game = updatedGame;
            ApplyLocalGameDetails(updatedGame);
            ApplyGameStatus(updatedGame.LocalAppState?.InstallStatus);
            ApplyOperation(FindOperation(updatedGame.AppName));
        });
    }

    private async Task LoadStoreDetails(
        string appName,
        string nameSpace,
        CancellationToken cancellationToken)
    {
        IsDetailsLoading = true;
        StoreProductDetails? details = null;
        try
        {
            details = await _libraryManager.GetStoreProductDetails(nameSpace, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            _log.LogWarning("Game Info Store details are unavailable");
        }

        _uiDispatcher.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested || Game?.AppName != appName) return;
            ApplyStoreDetails(details);
            IsDetailsLoading = false;
        });
    }

    private async Task LoadPlaytime(string appName, CancellationToken cancellationToken)
    {
        long? seconds = null;
        try
        {
            seconds = await _libraryManager.GetGamePlaytimeSeconds(appName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            _log.LogWarning("Game Info playtime is unavailable");
        }

        _uiDispatcher.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested || Game?.AppName != appName) return;
            TotalPlayTimeText = FormatPlaytime(seconds);
        });
    }

    internal static string FormatPlaytime(long? seconds)
    {
        if (seconds is null or < 0) return "Unavailable";
        if (seconds == 0) return "0m";
        if (seconds < 60) return "<1m";
        var minutes = seconds.Value / 60;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";
    }

    private void ApplyCatalogMetadata(Game game)
    {
        CoverImageUrl = FindImage(game.Metadata.KeyImages, "DieselGameBoxTall", "OfferImageTall", "DieselGameBox");
        HasCoverImage = !string.IsNullOrWhiteSpace(CoverImageUrl);
        HeroImageUrl = FindImage(game.Metadata.KeyImages, "DieselGameBox", "OfferImageWide");
        HasHeroImage = !string.IsNullOrWhiteSpace(HeroImageUrl);
        Screenshots = [];
        HasScreenshots = false;
        DeveloperName = FirstNonEmpty(game.Metadata.Developer, "Loading…");
        PublisherName = "Loading…";
        TagsText = string.Empty;
        HasTags = false;
        ReleaseDateText = "Loading…";
        TotalPlayTimeText = "Loading…";
        WindowsRequirements = [];
        HasWindowsRequirements = false;
        IsStoreDetailsUnavailable = false;
        ApplyDescription(game.Metadata.Description, game.AppTitle);
        ApplyLocalGameDetails(game);
    }

    private void ApplyStoreDetails(StoreProductDetails? details)
    {
        IsStoreDetailsUnavailable = details is null;
        DeveloperName = GetDetailText(FirstNonEmpty(details?.DeveloperName, Game?.Metadata.Developer));
        PublisherName = GetDetailText(details?.PublisherName);
        ReleaseDateText = details?.ReleaseDate?.ToString("d MMMM yyyy") ?? "Unavailable";
        if (details is null) return;

        HeroImageUrl = FirstNonEmpty(
            HeroImageUrl,
            FindImage(details.Images, "featuredMedia", "OfferImageWide"));
        HasHeroImage = !string.IsNullOrWhiteSpace(HeroImageUrl);
        Screenshots = GetScreenshots(details.Images);
        HasScreenshots = Screenshots.Count > 0;
        var tags = details.Tags.Take(8).ToList();
        TagsText = string.Join("  •  ", tags.Select(tag => tag.Name));
        HasTags = tags.Count > 0;
        WindowsRequirements = details.WindowsRequirements;
        HasWindowsRequirements = WindowsRequirements.Count > 0;
        ApplyDescription(
            StoreDescriptionFormatter.ToPlainText(details.LongDescription),
            Game?.AppTitle,
            details.ShortDescription);
    }

    private void ApplyDescription(string? preferred, string? title, string? fallback = null)
    {
        var description = FirstNonEmpty(preferred, fallback);
        if (string.Equals(description, title, StringComparison.OrdinalIgnoreCase))
            description = string.Empty;

        GameDescription = description;
        HasGameDescription = !string.IsNullOrWhiteSpace(description);
    }

    private void ApplyLocalGameDetails(Game game)
    {
        InstalledVersion = GetDetailText(game.LocalAppState?.Version);
        LastUpdatedText = game.LocalAppState?.LastUpdatedAt?.ToLocalTime().ToString("g") ?? "Unavailable";
    }

    internal static string GetDetailText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;

    private static string? FindImage(IEnumerable<KeyImage>? images, params string[] preferredTypes) =>
        FindImage(
            images?.Select(image => new StoreProductImage
            {
                Type = image.Type,
                Url = image.Url,
                IsVideo = false
            }) ?? [],
            preferredTypes);

    private static string? FindImage(
        IEnumerable<StoreProductImage> images,
        params string[] preferredTypes)
    {
        foreach (var preferredType in preferredTypes)
        {
            var url = images.FirstOrDefault(image =>
                !image.IsVideo &&
                string.Equals(image.Type, preferredType, StringComparison.OrdinalIgnoreCase) &&
                EpicEndpointPolicy.TryGetContentUri(image.Url, out _))?.Url;
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    internal static IReadOnlyList<StoreProductImage> GetScreenshots(
        IEnumerable<StoreProductImage> images) => images
        .Where(image =>
            !image.IsVideo &&
            string.Equals(image.Type, "featuredMedia", StringComparison.OrdinalIgnoreCase) &&
            EpicEndpointPolicy.TryGetContentUri(image.Url, out _))
        .DistinctBy(image => image.Url, StringComparer.OrdinalIgnoreCase)
        .Take(8)
        .ToList();

    private static string FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

    private void ApplyGameStatus(InstallState? installState)
    {
        IsProgressRingVisible = false;
        PrimaryAction = GetPrimaryAction(installState);
        IsPrimaryActionEnabled = PrimaryAction != GamePrimaryAction.None;
        PrimaryActionButtonText = PrimaryAction switch
        {
            GamePrimaryAction.Install => "Install",
            GamePrimaryAction.Play => "Play",
            GamePrimaryAction.Update => "Update",
            GamePrimaryAction.Repair => "Repair",
            _ => string.Empty
        };
        IsInstalled = PrimaryAction is not GamePrimaryAction.Install and not GamePrimaryAction.None;
        IsManageEnabled = IsInstalled;
        IsImportVisible = PrimaryAction == GamePrimaryAction.Install;
        IsImportEnabled = IsImportVisible;
        GameStatusText = GetGameStatusLabel(installState);
    }

    internal static string GetGameStatusLabel(InstallState? installState) => installState switch
    {
        InstallState.Installed => "Ready to play",
        InstallState.NeedUpdate => "Update available",
        InstallState.Broken => "Repair required",
        InstallState.Installing or InstallState.Updating or InstallState.Repairing => "Operation in progress",
        InstallState.InstallationPaused or InstallState.UpdatingPaused => "Operation paused",
        _ => "Not installed"
    };

    internal static GamePrimaryAction GetPrimaryAction(InstallState? installState) => installState switch
    {
        null or InstallState.NotInstalled => GamePrimaryAction.Install,
        InstallState.Installed => GamePrimaryAction.Play,
        InstallState.NeedUpdate => GamePrimaryAction.Update,
        InstallState.Broken => GamePrimaryAction.Repair,
        _ => GamePrimaryAction.None
    };

    public void OnNavigatedFrom()
    {
        UnregisterEventHandlers();
        CancelStoreDetailsRequest();
    }

    private bool CanRunPrimaryAction() => Game is not null &&
        GetPrimaryAction(Game.LocalAppState?.InstallStatus) != GamePrimaryAction.None &&
        FindOperation(Game.AppName) is null;

    private bool CanImport(Game? game) => game is not null &&
        GetPrimaryAction(game.LocalAppState?.InstallStatus) == GamePrimaryAction.Install &&
        FindOperation(game.AppName) is null;

    private bool CanManage(Game? game) => game is not null &&
        game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate or InstallState.Broken &&
        FindOperation(game.AppName) is null;

    [RelayCommand]
    private void Uninstall()
    {
        if (!CanManage(Game)) return;

        _storage.LocalAppStateDictionary.TryGetValue(Game.AppName, out var installedGame);

        if (installedGame == null)
        {
            _log.LogInformation("ProcessNext: Attempting to uninstall not installed game");
            return;
        }

        _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Uninstall, installedGame.InstallPath));
        _log.LogInformation("GameInfoPage: Added {Game} to Installation Queue", Game.AppTitle);
    }

    [RelayCommand]
    private void VerifyRepair()
    {
        if (!CanManage(Game)) return;

        _storage.LocalAppStateDictionary.TryGetValue(Game.AppName, out var installedGame);

        if (installedGame == null)
        {
            _log.LogInformation("VerifyRepair: Game not installed");
            return;
        }

        _log.LogInformation("GameInfoPage: Queueing verify/repair for {Game}", Game.AppTitle);
        _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Repair, installedGame.InstallPath));
    }

    [RelayCommand]
    private async Task ImportGameAsync()
    {
        var game = Game;
        if (!CanImport(game)) return;

        if (FolderPickerRequested == null)
        {
            ReportFolderPickerUnavailable(game.AppName, ActionType.Import);
            return;
        }

        var folderPath = await PickOperationFolderAsync(game.AppName, ActionType.Import);
        if (string.IsNullOrEmpty(folderPath) || !ReferenceEquals(Game, game) || !CanImport(game)) return;

        _installer.AddToQueue(new InstallItem(game.AppName, ActionType.Import, folderPath));
    }

    [RelayCommand]
    private async Task MoveGameAsync()
    {
        var game = Game;
        if (!CanManage(game)) return;

        if (FolderPickerRequested == null)
        {
            ReportFolderPickerUnavailable(game.AppName, ActionType.Move);
            return;
        }

        var folderPath = await PickOperationFolderAsync(game.AppName, ActionType.Move);
        if (string.IsNullOrEmpty(folderPath) || !ReferenceEquals(Game, game) || !CanManage(game)) return;

        try
        {
            var currentPath = game.LocalAppState.InstallPath;
            var destPath = Path.Combine(folderPath, game.AppName);
            _installer.AddToQueue(new InstallItem(game.AppName, ActionType.Move, currentPath)
            {
                MoveLocation = destPath
            });
        }
        catch (Exception exception)
        {
            _installer.ReportPreparationFailure(game.AppName, ActionType.Move, exception);
        }
    }

    private void ReportFolderPickerUnavailable(string appName, ActionType action) =>
        _installer.ReportPreparationFailure(appName, action, new OperationFailure(
            OperationFailureKind.Unknown, "folder_picker_unavailable", "The folder picker could not be opened.",
            "Restart Crimson and try again.", false));

    private async Task<string?> PickOperationFolderAsync(string appName, ActionType action)
    {
        try
        {
            return await FolderPickerRequested!.Invoke();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            _installer.ReportPreparationFailure(appName, action, exception);
            return null;
        }
    }

    public void Cleanup()
    {
        UnregisterEventHandlers();
        CancelStoreDetailsRequest();
    }
}

