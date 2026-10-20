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
    private readonly IArtworkCache? _artworkCache;
    private int _accountGeneration = -1;
    private readonly LibraryManager _libraryManager;
    private readonly IUiDispatcher _uiDispatcher;
    private int _navigationGeneration;
    private Action<IEnumerable<Game>>? _libraryUpdatedHandler;

    public LibraryViewModel(
        ILogger logger,
        LibraryManager libraryManager,
        IUiDispatcher uiDispatcher,
        IArtworkCache? artworkCache = null)
    {
        _log = logger;
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

    private async Task LoadArtworkAsync(LibraryItem item, int generation)
    {
        try
        {
            var uri = await _artworkCache!.GetImageUriAsync(item.SourceImageUrl);
            _uiDispatcher.TryEnqueue(() =>
            {
                if (generation == Volatile.Read(ref _navigationGeneration) &&
                    _accountGeneration == _libraryManager.AccountGeneration)
                    item.ImageUrl = uri;
            });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load library artwork");
        }
    }

    private void UpdateLibrary(IEnumerable<Game> games, int generation)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null || generation != Volatile.Read(ref _navigationGeneration)) return;

            var accountGeneration = _libraryManager.AccountGeneration;
            var snapshot = games.ToList();
            _uiDispatcher.TryEnqueue(() =>
            {
                if (generation != Volatile.Read(ref _navigationGeneration) ||
                    accountGeneration != _libraryManager.AccountGeneration) return;
                _accountGeneration = accountGeneration;
                var existing = GamesList.ToDictionary(item => item.Name, StringComparer.Ordinal);
                var items = new List<LibraryItem>();
                foreach (var game in snapshot)
                {
                    if (game.Metadata is null || game.IsDlc()) continue;
                    var image = game.Metadata.KeyImages?
                        .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
                        ?? game.Metadata.KeyImages?.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
                    if (!existing.TryGetValue(game.AppName, out var item) ||
                        item.Title != game.AppTitle || item.SourceImageUrl != image)
                    {
                        item = new LibraryItem
                        {
                            Name = game.AppName,
                            Title = game.AppTitle,
                            SourceImageUrl = image,
                            ImageUrl = _artworkCache is null ? image : null
                        };
                    }
                    items.Add(item);
                    if (_artworkCache is not null && item.ImageUrl is null && image is not null)
                        _ = LoadArtworkAsync(item, generation);
                }
                var sorted = items.OrderBy(item => item.Title).ToList();
                // Keep the existing ItemsSource and card objects on an unchanged refresh.
                if (!GamesList.SequenceEqual(sorted)) GamesList = sorted;
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
