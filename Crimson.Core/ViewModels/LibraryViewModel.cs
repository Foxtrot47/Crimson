using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Providers;
using Serilog;

namespace Crimson.ViewModels;

public partial class LibraryViewModel : ObservableObject, INavigationAware
{
    [ObservableProperty]
    private List<LibraryItem> _gamesList = [];

    [ObservableProperty]
    private bool _loadingFinished = false;

    [ObservableProperty]
    private bool _showLoadingScreen = true;

    [ObservableProperty]
    private bool _showAppGrid = false;

    [ObservableProperty]
    private bool _showQueueItems = false;

    private readonly ILogger _log;
    private readonly LauncherLibrary _libraryManager;
    private readonly LauncherRegistry _launchers;
    private readonly IUiDispatcher _uiDispatcher;
    private int _navigationGeneration;
    private Action<IEnumerable<LauncherGame>>? _libraryUpdatedHandler;

    [ObservableProperty]
    private bool _showEmptyLibrary;

    public LibraryViewModel(
        ILogger logger,
        LauncherLibrary libraryManager,
        LauncherRegistry launchers,
        IUiDispatcher uiDispatcher)
    {
        _log = logger;
        _libraryManager = libraryManager;
        _launchers = launchers;
        _uiDispatcher = uiDispatcher;
    }

    public async Task OnNavigatedTo(object parameter)
    {
        OnNavigatedFrom();
        var generation = Volatile.Read(ref _navigationGeneration);
        _libraryUpdatedHandler = games => UpdateLibrary(games, generation);
        _libraryManager.LibraryUpdated += _libraryUpdatedHandler;
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
        _libraryUpdatedHandler = null;
    }

    private void UpdateLibrary(IEnumerable<LauncherGame> games, int generation)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null || generation != Volatile.Read(ref _navigationGeneration)) return;

            _uiDispatcher.TryEnqueue(() =>
            {
                if (generation != Volatile.Read(ref _navigationGeneration)) return;
                GamesList = new List<LibraryItem>();
                foreach (var game in _libraryManager.CachedGames)
                {
                    if (game.IsDlc()) continue;
                    var item = new LibraryItem
                    {
                        Name = game.AppName,
                        Title = game.AppTitle,
                        ProviderName = _launchers.Get(game.Id.ProviderId).DisplayName,
                        ImageUrl = game.CoverImageUrl
                    };
                    _log.Debug("UpdateLibrary: Adding {AppName} to library", item.Name);
                    GamesList.Add(item);
                }
                GamesList = GamesList.OrderBy(item => item.Title).ToList();
                ShowEmptyLibrary = GamesList.Count == 0;
                ShowLoadingScreen = false;
                ShowAppGrid = true;
            });
            _log.Information("UpdateLibrary: Updated Library Page");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "UpdateLibrary failed");
        }
    }
}
