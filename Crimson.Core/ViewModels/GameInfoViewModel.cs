using System;
using System.IO;
using System.Linq;
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

    [ObservableProperty]
    private Game? _game;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isImportVisible;

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
    private string? _titleImageUrl;

    [ObservableProperty]
    private bool _isOperationStatusVisible;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    [ObservableProperty]
    private string _operationDetail = string.Empty;

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
        Game = _libraryManager.GetGameInfo(appName);
        if (Game is null) return;

        var gameImage = Game.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBox");
        TitleImageUrl = gameImage?.Url;

        _libraryManager.GameStatusUpdated += CheckGameStatus;
        _installer.OperationUpdated += HandleOperationUpdated;
        CheckGameStatus(Game);
    }

    private void UnregisterEventHandlers()
    {
        _libraryManager.GameStatusUpdated -= CheckGameStatus;
        _installer.OperationUpdated -= HandleOperationUpdated;
    }

    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        try
        {
            _log.LogInformation("GameInfoPage: Primary Action Button Clicked for {Game}", Game.AppTitle);
            if (Game == null) return;

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
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PrimaryActionAsync: Exception");
            IsProgressRingVisible = false;
            IsPrimaryActionEnabled = true;
        }
    }

    private void HandleOperationUpdated(OperationSnapshot operation)
    {
        try
        {
            if (Game is null || operation.AppName != Game.AppName) return;
            _uiDispatcher.TryEnqueue(() =>
            {
                if (Game?.AppName == operation.AppName)
                    ApplyOperation(operation);
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HandleOperationUpdated failed");
        }
    }

    private OperationSnapshot? FindOperation(string appName)
    {
        if (_installer.CurrentOperation is { } current && current.AppName == appName)
            return current;
        return _installer.GetQueueOperations().FirstOrDefault(operation => operation.AppName == appName);
    }

    private void ApplyOperation(OperationSnapshot? operation)
    {
        if (operation is null)
        {
            IsOperationStatusVisible = false;
            return;
        }

        var presentation = OperationPresenter.Present(operation);
        IsOperationStatusVisible = true;
        OperationStatusText = presentation.Headline;
        OperationDetail = presentation.Detail;
        IsProgressRingVisible = presentation.IsActive;
        IsProgressRingIndeterminate = presentation.IsIndeterminate;
        ProgressValue = presentation.ProgressPercentage;
        IsPrimaryActionEnabled = !presentation.IsActive;

        if (presentation.IsActive)
            PrimaryActionButtonText = presentation.Headline;
        else if (Game is not null)
            ApplyGameStatus(Game.LocalAppState?.InstallStatus);
    }

    private void CheckGameStatus(Game updatedGame)
    {
        if (updatedGame == null || Game is null || updatedGame.AppName != Game.AppName) return;
        _log.LogInformation("GameInfoPage: Game Status Changed for {Game}", updatedGame.AppTitle);
        var operation = FindOperation(updatedGame.AppName);

        _uiDispatcher.TryEnqueue(() =>
        {
            if (Game?.AppName != updatedGame.AppName) return;
            Game = updatedGame;
            ApplyGameStatus(updatedGame.LocalAppState?.InstallStatus);
            ApplyOperation(operation);
        });
    }

    private void ApplyGameStatus(InstallState? installState)
    {
        IsProgressRingVisible = false;
        IsPrimaryActionEnabled = true;
        PrimaryAction = GetPrimaryAction(installState);
        PrimaryActionButtonText = PrimaryAction switch
        {
            GamePrimaryAction.Install => "Install",
            GamePrimaryAction.Play => "Play",
            GamePrimaryAction.Update => "Update",
            GamePrimaryAction.Repair => "Repair",
            _ => string.Empty
        };
        IsInstalled = PrimaryAction is not GamePrimaryAction.Install and not GamePrimaryAction.None;
        IsImportVisible = PrimaryAction == GamePrimaryAction.Install;
    }

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

    }

    [RelayCommand]
    private void Uninstall()
    {
        if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

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
        if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

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
        if (Game == null) return;

        if (FolderPickerRequested == null)
        {
            _log.LogWarning("ImportGame: No folder picker handler registered");
            return;
        }

        var folderPath = await FolderPickerRequested.Invoke();
        if (string.IsNullOrEmpty(folderPath)) return;

        _log.LogInformation("GameInfoPage: Importing {Game} from {Path}", Game.AppTitle, folderPath);
        _installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Import, folderPath));
    }

    [RelayCommand]
    private async Task MoveGameAsync()
    {
        if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

        if (FolderPickerRequested == null)
        {
            _log.LogWarning("MoveGame: No folder picker handler registered");
            return;
        }

        var folderPath = await FolderPickerRequested.Invoke();
        if (string.IsNullOrEmpty(folderPath)) return;

        var currentPath = Game.LocalAppState.InstallPath;
        var destPath = Path.Combine(folderPath, Game.AppName);

        _log.LogInformation("GameInfoPage: Moving {Game} from {Src} to {Dest}", Game.AppTitle, currentPath, destPath);
        var item = new InstallItem(Game.AppName, ActionType.Move, currentPath)
        {
            MoveLocation = destPath
        };
        _installer.AddToQueue(item);
    }

    public void Cleanup()
    {
        UnregisterEventHandlers();
    }
}

