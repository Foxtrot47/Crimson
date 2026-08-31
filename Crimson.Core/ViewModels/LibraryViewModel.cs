using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly LibraryManager _libraryManager;
    private readonly IUiDispatcher _uiDispatcher;

    public LibraryViewModel(
        ILogger logger,
        LibraryManager libraryManager,
        IUiDispatcher uiDispatcher)
    {
        _log = logger;
        _libraryManager = libraryManager;
        _uiDispatcher = uiDispatcher;
    }

    public async Task OnNavigatedTo(object parameter)
    {
        _log.Information("LibraryPage: Loading Page");
        var games = await _libraryManager.GetLibraryData();
        UpdateLibrary(games);
        _libraryManager.LibraryUpdated += UpdateLibrary;
        _log.Information("LibraryPage: Loading finished");
    }

    public void OnNavigatedFrom()
    {
        _libraryManager.LibraryUpdated -= UpdateLibrary;
    }

    private void UpdateLibrary(IEnumerable<Game> games)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null) return;

            _uiDispatcher.TryEnqueue(() =>
            {
                GamesList = new List<LibraryItem>();
                foreach (var game in games)
                {
                    if (game.IsDlc()) continue;
                    var item = new LibraryItem
                    {
                        Name = game.AppName,
                        Title = game.AppTitle,
                        //InstallState = game.State,
                        ImageUrl = game.Metadata.KeyImages
                            .FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
                    };
                    _log.Debug("UpdateLibrary: Adding {AppName} to library", item.Name);
                    GamesList.Add(item);
                }
                GamesList = GamesList.OrderBy(item => item.Title).ToList();
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
