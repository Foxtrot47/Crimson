using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
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

    public bool HasNoInstalledGames => InstalledOnly && !ShowLoadingScreen && GamesList.Count == 0;

    partial void OnInstalledOnlyChanged(bool value)
    {
        if (_libraryUpdatedHandler is not null && !ShowLoadingScreen)
            RefreshOperationStatus(Volatile.Read(ref _navigationGeneration));
    }

    private readonly ILogger _log;
    private readonly InstallManager _installManager;
    private readonly IArtworkCache? _artworkCache;
    private int _accountGeneration = -1;
    private readonly LibraryManager _libraryManager;
    private readonly IUiDispatcher _uiDispatcher;
    private int _navigationGeneration;
    private Action<IEnumerable<Game>>? _libraryUpdatedHandler;
    private Action<OperationSnapshot>? _operationUpdatedHandler;
    private IReadOnlyList<Game> _librarySnapshot = [];

    public LibraryViewModel(
        ILogger logger,
        InstallManager installManager,
        LibraryManager libraryManager,
        IUiDispatcher uiDispatcher,
        IArtworkCache? artworkCache = null)
    {
        _log = logger;
        _installManager = installManager;
        _artworkCache = artworkCache;
        _libraryManager = libraryManager;
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

    private void UpdateLibrary(IEnumerable<Game> games, int generation)
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
        IReadOnlyList<Game> games,
        IReadOnlyDictionary<string, OperationSnapshot> operations,
        int generation,
        bool retryArtwork)
    {
        if (generation != Volatile.Read(ref _navigationGeneration) ||
            _accountGeneration != _libraryManager.AccountGeneration) return;

        var existing = GamesList.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var items = games
            .Where(game => !game.IsDlc() && MatchesInstalledFilter(game))
            .Select(game => CreateOrUpdateLibraryItem(game, operations.GetValueOrDefault(game.AppName),
                existing.GetValueOrDefault(game.AppName), generation, retryArtwork))
            .OrderBy(item => item.Title)
            .ToList();
        if (!GamesList.SequenceEqual(items)) GamesList = items;
        ShowLoadingScreen = false;
        ShowAppGrid = true;
    }

    private bool MatchesInstalledFilter(Game game)
    {
        if (!InstalledOnly) return true;
        var current = _libraryManager.GetGameInfo(game.AppName);
        return current is not null && LibraryManager.IsSidebarListed(current, null);
    }

    private LibraryItem CreateOrUpdateLibraryItem(
        Game game, OperationSnapshot? operation, LibraryItem? item, int generation, bool retryArtwork)
    {
        var imageUrl = game.Metadata?.KeyImages?
            .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
            ?? game.Metadata?.KeyImages?.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
        var artworkGeneration = _artworkCache?.Generation ?? 0;
        if (item is null || item.Title != game.AppTitle || item.SourceImageUrl != imageUrl ||
            item.ArtworkGeneration != artworkGeneration)
        {
            item = new LibraryItem
            {
                Name = game.AppName,
                Title = game.AppTitle,
                SourceImageUrl = imageUrl,
                ArtworkGeneration = artworkGeneration,
                ImageUrl = _artworkCache is null ? imageUrl : null
            };
        }

        if (_artworkCache is not null && item.ImageUrl is null && imageUrl is not null &&
            !item.ArtworkLoading && (!item.ArtworkAttempted || retryArtwork))
        {
            item.ArtworkLoading = true;
            item.ArtworkAttempted = true;
            _ = LoadArtworkAsync(item, generation);
        }
        UpdateItemOperation(item, operation);
        return item;
    }

    private static void UpdateItemOperation(LibraryItem item, OperationSnapshot? operation)
    {
        var presentation = operation is null ? null : OperationPresenter.Present(operation);
        item.OperationStatus = presentation?.Headline ?? string.Empty;
        item.HasActiveOperation = presentation?.IsActive == true;
        item.IsOperationIndeterminate = presentation?.IsIndeterminate == true;
        item.OperationProgress = presentation?.ProgressPercentage ?? 0;
    }
}
