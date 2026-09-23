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
    private List<LibraryItem> _gamesList;

    [ObservableProperty]
    private bool _loadingFinished = false;

    [ObservableProperty]
    private bool _showLoadingScreen = true;

    [ObservableProperty]
    private bool _showAppGrid = false;

    [ObservableProperty]
    private bool _showQueueItems = false;

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
        var operations = _installManager.GetQueueOperations()
            .Where(operation => operation.IsActive)
            .ToDictionary(operation => operation.AppName, StringComparer.Ordinal);
        if (_installManager.CurrentOperation is { IsActive: true } current)
            operations[current.AppName] = current;
        return operations;
    }

    private void ApplyLibrary(
        IReadOnlyList<Game> games,
        IReadOnlyDictionary<string, OperationSnapshot> operations,
        int generation)
    {
        if (generation != Volatile.Read(ref _navigationGeneration)) return;

        GamesList = games
            .Where(game => !game.IsDlc())
            .Select(game => CreateLibraryItem(game, operations.GetValueOrDefault(game.AppName)))
            .OrderBy(item => item.Title)
            .ToList();
        ShowLoadingScreen = false;
        ShowAppGrid = true;
    }

    private LibraryItem CreateLibraryItem(Game game, OperationSnapshot? operation)
    {
        var presentation = operation is null ? null : OperationPresenter.Present(operation);
        var item = new LibraryItem
        {
            Name = game.AppName,
            Title = game.AppTitle,
            ImageUrl = game.Metadata.KeyImages
                .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url,
            OperationStatus = presentation?.Headline ?? string.Empty,
            HasActiveOperation = presentation?.IsActive == true,
            IsOperationIndeterminate = presentation?.IsIndeterminate == true,
            OperationProgress = presentation?.ProgressPercentage ?? 0
        };
        _log.Debug("UpdateLibrary: Adding {AppName} to library", item.Name);
        return item;
    }
}
