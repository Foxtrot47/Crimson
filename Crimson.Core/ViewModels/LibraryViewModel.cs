using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Providers;
using Crimson.Resources;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class LibraryViewModel : ObservableObject, INavigationAware
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoInstalledGames))]
    private List<LibraryItem> _gamesList = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoInstalledGames))]
    private bool _installedOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView))]
    private bool _isListView;

    public bool IsGridView => !IsListView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    private string _actionErrorMessage = string.Empty;

    public bool HasActionError => !string.IsNullOrWhiteSpace(ActionErrorMessage);

    [ObservableProperty]
    private bool _loadingFinished = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoInstalledGames))]
    private bool _showLoadingScreen = true;

    [ObservableProperty]
    private bool _showAppGrid = false;

    [ObservableProperty]
    private bool _showQueueItems = false;

    [ObservableProperty]
    private bool _showEmptyLibrary;

    public bool HasNoInstalledGames => InstalledOnly && !ShowLoadingScreen && GamesList.Count == 0;

    /// <summary>Raised when a card's install action needs the page to show the install dialog.</summary>
    public event Func<LauncherGame, Task>? ShowInstallDialogRequested;

    partial void OnInstalledOnlyChanged(bool value)
    {
        if (_libraryUpdatedHandler is not null && !ShowLoadingScreen)
            RefreshOperationStatus(Volatile.Read(ref _navigationGeneration));
    }

    private readonly ILogger _log;
    private readonly LauncherInstaller _installManager;
    private readonly IArtworkCache? _artworkCache;
    private int _accountGeneration = -1;
    private readonly LauncherLibrary _libraryManager;
    private readonly LauncherRegistry _launchers;
    private readonly IUiDispatcher _uiDispatcher;
    private int _navigationGeneration;
    private Action<IEnumerable<LauncherGame>>? _libraryUpdatedHandler;
    private Action<OperationSnapshot>? _operationUpdatedHandler;
    private IReadOnlyList<LauncherGame> _librarySnapshot = [];
    private int _artworkFailures;

    public LibraryViewModel(
        ILogger logger,
        LauncherInstaller installManager,
        LauncherLibrary libraryManager,
        LauncherRegistry launchers,
        IUiDispatcher uiDispatcher,
        IArtworkCache? artworkCache = null)
    {
        _log = logger;
        _installManager = installManager;
        _artworkCache = artworkCache;
        _libraryManager = libraryManager;
        _launchers = launchers;
        _uiDispatcher = uiDispatcher;
    }

    public async Task OnNavigatedTo(object parameter)
    {
        OnNavigatedFrom();
        var generation = Volatile.Read(ref _navigationGeneration);
        if (_accountGeneration != _libraryManager.AccountGeneration)
        {
            _accountGeneration = _libraryManager.AccountGeneration;
            GamesList = [];
            ShowLoadingScreen = true;
            ShowAppGrid = false;
        }
        if (_artworkCache is null)
            _log.Warning("Artwork cache is unavailable; library images will not load");
        _libraryUpdatedHandler = games => UpdateLibrary(games, generation);
        _operationUpdatedHandler = _ => RefreshOperationStatus(generation);
        _libraryManager.LibraryUpdated += _libraryUpdatedHandler;
        _installManager.OperationUpdated += _operationUpdatedHandler;
        _log.Information("LibraryPage: Loading Page");
        var games = await _libraryManager.GetLibraryData();
        if (games.Any() || !_libraryManager.IsAuthenticationCheckInProgress)
            UpdateLibrary(games, generation);
        _log.Information("LibraryPage: Loading finished");
    }

    public void OnNavigatedFrom()
    {
        Interlocked.Increment(ref _navigationGeneration);
        if (_libraryUpdatedHandler is not null)
            _libraryManager.LibraryUpdated -= _libraryUpdatedHandler;
        if (_operationUpdatedHandler is not null)
            _installManager.OperationUpdated -= _operationUpdatedHandler;
        _libraryUpdatedHandler = null;
        _operationUpdatedHandler = null;
    }

    private async Task LoadArtworkAsync(LibraryItem item, int generation)
    {
        string? uri = null;
        try
        {
            uri = await _artworkCache!.GetImageUriAsync(item.SourceImageUrl);
            if (uri is null && Interlocked.Increment(ref _artworkFailures) <= 3)
                _log.Warning("Library artwork was not cached for {Url}", item.SourceImageUrl);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load library artwork");
        }
        _uiDispatcher.TryEnqueue(() =>
        {
            // A missing image is requested again on the next library update; the artwork
            // cache's failure cooldown bounds how often that reaches the network.
            item.ArtworkLoading = false;
            if (generation == Volatile.Read(ref _navigationGeneration) &&
                _accountGeneration == _libraryManager.AccountGeneration)
                item.ImageUrl = uri;
        });
    }

    private void UpdateLibrary(IEnumerable<LauncherGame> games, int generation)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null || generation != Volatile.Read(ref _navigationGeneration)) return;

            _librarySnapshot = games.ToList();
            _accountGeneration = _libraryManager.AccountGeneration;
            RefreshOperationStatus(generation, retryArtwork: true);
            _log.Information("UpdateLibrary: Updated Library Page");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "UpdateLibrary failed");
        }
    }

    // Only library updates retry missing artwork; operation progress fires too often.
    private void RefreshOperationStatus(int generation, bool retryArtwork = false)
    {
        try
        {
            if (generation != Volatile.Read(ref _navigationGeneration)) return;

            var games = _librarySnapshot;
            var operations = GetActiveOperations();
            _uiDispatcher.TryEnqueue(() => ApplyLibrary(games, operations, generation, retryArtwork));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RefreshOperationStatus failed");
        }
    }

    private IReadOnlyDictionary<string, OperationSnapshot> GetActiveOperations()
    {
        var operations = new Dictionary<string, OperationSnapshot>(StringComparer.Ordinal);
        foreach (var operation in _installManager.GetQueueOperations().Where(item => item.IsActive))
        {
            var appName = _libraryManager.GetBaseGameAppName(operation.AppName);
            operations.TryAdd(appName, operation);
        }

        if (_installManager.CurrentOperation is { IsActive: true } current)
            operations[_libraryManager.GetBaseGameAppName(current.AppName)] = current;
        return operations;
    }

    private void ApplyLibrary(
        IReadOnlyList<LauncherGame> games,
        IReadOnlyDictionary<string, OperationSnapshot> operations,
        int generation,
        bool retryArtwork)
    {
        if (generation != Volatile.Read(ref _navigationGeneration) ||
            _accountGeneration != _libraryManager.AccountGeneration) return;

        var existing = GamesList.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var items = games
            // Re-read every game so the list reflects current installed state, not a stale snapshot.
            .Select(game => _libraryManager.GetGameInfo(game.AppName) ?? game)
            .Where(game => !game.IsDlc() && MatchesInstalledFilter(game))
            .Select(game => CreateOrUpdateLibraryItem(game, operations.GetValueOrDefault(game.AppName),
                existing.GetValueOrDefault(game.AppName), generation, retryArtwork))
            .OrderBy(item => item.Title)
            .ToList();
        if (!GamesList.SequenceEqual(items)) GamesList = items;
        if (items.Count > 0)
            _log.Information("Library: {Cards} cards, {WithArtwork} with artwork sources; per provider: {Providers}",
                items.Count, items.Count(item => item.SourceImageUrl is not null),
                string.Join("; ", items.GroupBy(item => item.ProviderName)
                    .Select(group => $"{group.Key}: {group.Count(item => item.SourceImageUrl is not null)}/{group.Count()}")));
        foreach (var missing in items.Where(item => item.SourceImageUrl is null).Take(5))
            _log.Information("Library: {Provider} card '{Title}' has no artwork source",
                missing.ProviderName, missing.Title);
        ShowEmptyLibrary = items.Count == 0;
        ShowLoadingScreen = false;
        ShowAppGrid = true;
    }

    private bool MatchesInstalledFilter(LauncherGame game) => !InstalledOnly || IsInstalled(game);

    // Providers report presence through the normalized installation state, never through store metadata.
    private static bool IsInstalled(LauncherGame game) => game.LocalAppState?.InstallStatus is
        InstallState.Installed or InstallState.NeedUpdate or InstallState.Broken or InstallState.Playing
        or InstallState.Installing or InstallState.InstallationPaused
        or InstallState.Updating or InstallState.UpdatingPaused or InstallState.Repairing;

    private LibraryItem CreateOrUpdateLibraryItem(
        LauncherGame game, OperationSnapshot? operation, LibraryItem? item, int generation, bool retryArtwork)
    {
        var imageUrl = game.CoverImageUrl ?? game.LandscapeImageUrl;
        item = RebuildItemIfStale(item, game, imageUrl, _artworkCache?.Generation ?? 0);
        RequestArtwork(item, imageUrl, generation, retryArtwork);
        UpdateItemOperation(item, operation);
        UpdateItemAction(item, game, operation);
        return item;
    }

    // Card identity rarely changes; reuse the instance so bound cards keep their state.
    private LibraryItem RebuildItemIfStale(LibraryItem? item, LauncherGame game, string? imageUrl, int artworkGeneration)
    {
        if (item is not null && item.Title == game.AppTitle && item.SourceImageUrl == imageUrl &&
            item.ArtworkGeneration == artworkGeneration)
            return item;

        return new LibraryItem
        {
            Name = game.AppName,
            Title = game.AppTitle,
            ProviderName = _launchers.Get(game.Id.ProviderId).DisplayName,
            SourceImageUrl = imageUrl,
            ArtworkGeneration = artworkGeneration,
            ImageUrl = _artworkCache is null ? imageUrl : null
        };
    }

    private void RequestArtwork(LibraryItem item, string? imageUrl, int generation, bool retryArtwork)
    {
        if (_artworkCache is null || item.ImageUrl is not null || imageUrl is null) return;
        if (item.ArtworkLoading || (item.ArtworkAttempted && !retryArtwork)) return;

        item.ArtworkLoading = true;
        item.ArtworkAttempted = true;
        _ = LoadArtworkAsync(item, generation);
    }

    // A card offers only what it can do right now: an active operation owns the card's action row.
    private void UpdateItemAction(LibraryItem item, LauncherGame game, OperationSnapshot? operation)
    {
        var action = operation is null
            ? GameInfoViewModel.GetPrimaryAction(game.LocalAppState?.InstallStatus)
            : GamePrimaryAction.None;
        item.PrimaryAction = action;
        item.PrimaryActionText = DescribeAction(action);
        item.HasPrimaryAction = action != GamePrimaryAction.None;
        item.IsPrimaryActionEnabled = CanRunAction(game, action);
        item.PrimaryActionCommand ??= new AsyncRelayCommand(() => RunPrimaryActionAsync(item));
    }

    // Read the localized labels per call: the app can switch language while the library lives.
    private static string DescribeAction(GamePrimaryAction action) => action switch
    {
        GamePrimaryAction.Install => UiStrings.Install,
        GamePrimaryAction.Play => UiStrings.Play,
        GamePrimaryAction.Update => UiStrings.Update,
        GamePrimaryAction.Repair => UiStrings.Repair,
        _ => string.Empty
    };

    private bool CanRunAction(LauncherGame game, GamePrimaryAction action) => action switch
    {
        GamePrimaryAction.Play => game.CanLaunch,
        GamePrimaryAction.Install => game.CanInstall && _installManager.Supports(game.AppName, ActionType.Install),
        GamePrimaryAction.Update => _installManager.Supports(game.AppName, ActionType.Update),
        GamePrimaryAction.Repair => _installManager.Supports(game.AppName, ActionType.Repair),
        _ => false
    };

    private async Task RunPrimaryActionAsync(LibraryItem item)
    {
        var game = _librarySnapshot.FirstOrDefault(candidate => candidate.AppName == item.Name);
        if (game is null || _installManager.GetActiveOperationForGame(game.AppName) is not null)
            return;
        if (item.PrimaryAction == GamePrimaryAction.None)
            return;

        try
        {
            ActionErrorMessage = string.Empty;
            if (item.PrimaryAction == GamePrimaryAction.Install)
            {
                await RequestInstallAsync(game);
                return;
            }

            if (item.PrimaryAction == GamePrimaryAction.Play)
            {
                await _libraryManager.LaunchApp(game.AppName);
                return;
            }

            _installManager.AddToQueue(new InstallItem(
                game.AppName, ToActionType(item.PrimaryAction), game.LocalAppState?.InstallPath ?? string.Empty));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Library primary action failed for {Game}", game.AppTitle);
            _installManager.ReportPreparationFailure(game.AppName, ToActionType(item.PrimaryAction), ex);
            RefreshOperationStatus(Volatile.Read(ref _navigationGeneration));
        }
    }

    private async Task RequestInstallAsync(LauncherGame game)
    {
        if (ShowInstallDialogRequested is not null)
        {
            await ShowInstallDialogRequested.Invoke(game);
            return;
        }

        _installManager.ReportPreparationFailure(game.AppName, ActionType.Install,
            new OperationFailure(OperationFailureKind.Unknown, "install_dialog_unavailable",
                UiStrings.TheInstallationDialogCouldNotBeOpened,
                UiStrings.RestartCrimsonAndTryAgain, false));
    }

    private static ActionType ToActionType(GamePrimaryAction action) => action switch
    {
        GamePrimaryAction.Update => ActionType.Update,
        GamePrimaryAction.Repair => ActionType.Repair,
        _ => ActionType.Install
    };

    private static void UpdateItemOperation(LibraryItem item, OperationSnapshot? operation)
    {
        var presentation = operation is null ? null : OperationPresenter.Present(operation);
        item.OperationStatus = presentation?.Headline ?? string.Empty;
        item.HasActiveOperation = presentation?.IsActive == true;
        item.IsOperationIndeterminate = presentation?.IsIndeterminate == true;
        item.OperationProgress = presentation?.ProgressPercentage ?? 0;
    }
}
