using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
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
        IUiDispatcher uiDispatcher)
    {
        _log = logger;
        _installManager = installManager;
        _libraryManager = libraryManager;
        _uiDispatcher = uiDispatcher;
    }

    public async Task OnNavigatedTo(object parameter)
    {
        OnNavigatedFrom();
        var generation = Volatile.Read(ref _navigationGeneration);
        _libraryUpdatedHandler = games => UpdateLibrary(games, generation);
        _operationUpdatedHandler = _ => RefreshOperationStatus(generation);
        _libraryManager.LibraryUpdated += _libraryUpdatedHandler;
        _installManager.OperationUpdated += _operationUpdatedHandler;
        _log.Information("LibraryPage: Loading Page");
        var games = await _libraryManager.GetLibraryData();
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

    private void UpdateLibrary(IEnumerable<Game> games, int generation)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null || generation != Volatile.Read(ref _navigationGeneration)) return;

            _librarySnapshot = games.ToList();
            RefreshOperationStatus(generation);
            _log.Information("UpdateLibrary: Updated Library Page");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "UpdateLibrary failed");
        }
    }

    private void RefreshOperationStatus(int generation)
    {
        try
        {
            if (generation != Volatile.Read(ref _navigationGeneration)) return;

            var games = _librarySnapshot;
            var operations = GetActiveOperations();
            _uiDispatcher.TryEnqueue(() => ApplyLibrary(games, operations, generation));
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
        int generation)
    {
        if (generation != Volatile.Read(ref _navigationGeneration)) return;

        var existing = GamesList.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var items = games
            .Where(game => !game.IsDlc() && MatchesInstalledFilter(game))
            .Select(game => CreateOrUpdateLibraryItem(game, operations.GetValueOrDefault(game.AppName),
                existing.GetValueOrDefault(game.AppName)))
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

    private static LibraryItem CreateOrUpdateLibraryItem(Game game, OperationSnapshot? operation, LibraryItem? item)
    {
        var imageUrl = game.Metadata.KeyImages
            .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url;
        if (item is null || item.Title != game.AppTitle || item.ImageUrl != imageUrl)
            item = new LibraryItem { Name = game.AppName, Title = game.AppTitle, ImageUrl = imageUrl };

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
