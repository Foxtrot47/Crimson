using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Utils;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Crimson.ViewModels;

/// <summary>
/// Page for Showing Details of individual game and allowing play
/// download and other options
/// </summary>
public partial class GameInfoViewModel : ObservableObject, INavigationAware
{
    private readonly Windows.System.DispatcherQueue _dispatcherQueue;
    private readonly InstallManager _installer;
    private readonly LibraryManager _libraryManager;
    private readonly Storage _storage;
    private readonly ILogger _log;

    [ObservableProperty]
    private Game _game;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private string _primaryActionButtonText;

    [ObservableProperty]
    private string _primaryActionButtonGlyph;

    [ObservableProperty]
    private bool _isPrimaryActionEnabled = true;

    [ObservableProperty]
    private bool _isProgressRingVisible;

    [ObservableProperty]
    private bool _isProgressRingIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private BitmapImage _titleImage;

    // Event for showing install dialog
    public event Func<Task> ShowInstallDialogRequested;
    public event Action CloseInstallDialogRequested;

    // Event for requesting folder picker from view
    public event Func<Task<string>> FolderPickerRequested;

    public GameInfoViewModel(ILogger logger,
            InstallManager installer,
            LibraryManager libraryManager,
            Storage storage)
    {
        _dispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
        _log = logger;
        _installer = installer;
        _libraryManager = libraryManager;
        _storage = storage;

        _libraryManager.GameStatusUpdated += CheckGameStatus;
        _installer.InstallationStatusChanged += HandleInstallationStatusChanged;
        _installer.InstallProgressUpdate += HandleInstallationStatusChanged;
    }

    public async Task OnNavigatedTo(object parameter)
    {
        if (parameter is not string appName) return;

        Game = _libraryManager.GetGameInfo((string)appName);
        var gameImage = Game.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBox");
        TitleImage = gameImage != null ? new BitmapImage(new Uri(gameImage.Url)) : null;

        CheckGameStatus(Game);

        // Unregister event handlers on start
        UnregisterEventHandlers();

        _libraryManager.GameStatusUpdated += CheckGameStatus;
        _installer.InstallationStatusChanged += HandleInstallationStatusChanged;
        _installer.InstallProgressUpdate += HandleInstallationStatusChanged;

    }

    private void UnregisterEventHandlers()
    {
        _libraryManager.GameStatusUpdated -= CheckGameStatus;
        _installer.InstallationStatusChanged -= HandleInstallationStatusChanged;
        _installer.InstallProgressUpdate -= HandleInstallationStatusChanged;
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        try
        {
            _log.Information("GameInfoPage: Primary Action Button Clicked for {Game}", Game.AppTitle);
            if (Game == null) return;
            if (Game.InstallStatus == InstallState.Installed)
            {
                _log.Information("GameInfoPage: Starting Game {Game}", Game.AppTitle);
                await _libraryManager.LaunchApp(Game.AppName);
                return;
            }

            if (ShowInstallDialogRequested != null)
            {
                await ShowInstallDialogRequested.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            IsProgressRingVisible = false;
            IsPrimaryActionEnabled = true;
        }
    }

    /// <summary>
    /// Handing Installation State Change.
    /// <br/>
    /// This function is never run on UI Thread.
    /// <br/>
    /// So always make sure to use Dispatcher Queue to update UI thread
    /// </summary>
    /// <param name="installItem"></param>
    private void HandleInstallationStatusChanged(InstallItem installItem)
    {
        try
        {
            if (installItem == null || installItem.AppName != Game.AppName) return;
            _dispatcherQueue.TryEnqueue(() =>
            {
                _log.Information("GameInfoPage: Installation Status Changed for {Game}", installItem.AppName);
                switch (installItem.Status)
                {
                    case ActionStatus.Processing:
                        IsProgressRingIndeterminate = false;
                        ProgressValue = Convert.ToDouble(installItem.ProgressPercentage);
                        IsProgressRingVisible = true;
                        IsPrimaryActionEnabled = false;
                        PrimaryActionButtonText = $"{installItem.ProgressPercentage}%";
                        break;
                    case ActionStatus.Pending:
                        PrimaryActionButtonText = "Pending...";
                        IsProgressRingVisible = true;
                        IsProgressRingIndeterminate = true;
                        break;

                    case ActionStatus.Cancelling:
                        PrimaryActionButtonText = "Cancelling...";
                        IsProgressRingVisible = true;
                        IsProgressRingIndeterminate = true;
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
        }
    }

    private void CheckGameStatus(Game updatedGame)
    {
        if (updatedGame == null || updatedGame.AppName != Game.AppName) return;
        _log.Information("GameInfoPage: Game Status Changed for {Game}", updatedGame.AppTitle);
        Game = updatedGame;

        _dispatcherQueue.TryEnqueue(() =>
        {
            PrimaryActionButtonGlyph = "";
            IsProgressRingVisible = false;
            IsPrimaryActionEnabled = true;

            switch (Game.InstallStatus)
            {
                case InstallState.NotInstalled:
                    PrimaryActionButtonText = "Install";
                    PrimaryActionButtonGlyph = "\uE896";
                    IsInstalled = false;
                    break;
                case InstallState.Installed:
                    PrimaryActionButtonText = "Play";
                    PrimaryActionButtonGlyph = "\uE768";
                    IsInstalled = true;
                    break;
                case InstallState.NeedUpdate:
                    PrimaryActionButtonText = "Update";
                    PrimaryActionButtonGlyph = "\uE777";
                    IsInstalled = true;
                    break;
                case InstallState.Broken:
                    PrimaryActionButtonText = "Repair";
                    PrimaryActionButtonGlyph = "\uE90F";
                    IsInstalled = true;
                    break;
            }
        });
    }

    public void OnNavigatedFrom()
    {
        UnregisterEventHandlers();

    }

    [RelayCommand]
    private void Uninstall()
    {
        if (Game == null || Game.InstallStatus == InstallState.NotInstalled) return;

        _storage.LocalAppStateDictionary.TryGetValue(Game.AppName, out var installedGame);

        if (installedGame == null)
        {
            _log.Information("ProcessNext: Attempting to uninstall not installed game");
            return;
        }

        _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Uninstall, installedGame.InstallPath));
        _log.Information("GameInfoPage: Added {Game} to Installation Queue", Game.AppTitle);
    }

    public void Cleanup()
    {
        _libraryManager.GameStatusUpdated -= CheckGameStatus;
        _installer.InstallationStatusChanged -= HandleInstallationStatusChanged;
        _installer.InstallProgressUpdate -= HandleInstallationStatusChanged;
    }
}

