using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Models;
using Crimson.Views;
using Serilog;

namespace Crimson.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    [ObservableProperty]
    private static List<LibraryItem> s_gamesList;

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
    private readonly Windows.System.DispatcherQueue _dispatcherQueue;

    public LibraryViewModel()
    {
        _log = App.GetService<ILogger>();
        _libraryManager = App.GetService<LibraryManager>();
        _log.Information("LibraryPage: Loading Page");


        Task.Run(async () =>
        {
            var games = await _libraryManager.GetLibraryData();
            UpdateLibrary(games);
        });

        _libraryManager.LibraryUpdated += UpdateLibrary;
        _log.Information("LibraryPage: Loading finished");
        _dispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
    }

    private void UpdateLibrary(IEnumerable<Game> games)
    {
        try
        {
            _log.Information("UpdateLibrary: Updating Library Page");
            if (games == null) return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                S_gamesList = new List<LibraryItem>();
                foreach (var game in games)
                {
                    if (game.IsDlc()) continue;
                    var item = new LibraryItem
                    {
                        Name = game.AppName,
                        Title = game.AppTitle,
                        //InstallState = game.State,
                        Image = Util.GetBitmapImage(game.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url)
                    };
                    _log.Information($"UpdateLibrary: Adding {item.Name} to Library");
                    S_gamesList.Add(item);
                }
                S_gamesList = S_gamesList.OrderBy(item => item.Title).ToList();
                ShowLoadingScreen = false;
                ShowAppGrid = true;
            });
            _log.Information("UpdateLibrary: Updated Library Page");
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
        }
    }
}
