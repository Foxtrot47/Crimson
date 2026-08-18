using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Models;

namespace Crimson.Presentation;

public enum GameActionIcon
{
    None,
    Install,
    Play,
    Update,
    Repair
}

public partial class GameInfoViewModel : ObservableObject, IDisposable
{
    private readonly IGameWorkflowService _games;
    private readonly IInstallDialogService _installDialog;
    private readonly IFolderPickerService _folderPicker;
    private readonly IUiDispatcher _dispatcher;
    private string? _appName;
    private bool _active;
    private bool _disposed;

    [ObservableProperty]
    private GamePresentationData? _game;

    [ObservableProperty]
    private Uri? _titleImageUri;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isImportVisible;

    [ObservableProperty]
    private string _primaryActionButtonText = "Install";

    [ObservableProperty]
    private GameActionIcon _primaryActionIcon = GameActionIcon.Install;

    [ObservableProperty]
    private bool _isPrimaryActionEnabled = true;

    [ObservableProperty]
    private bool _isProgressRingVisible;

    [ObservableProperty]
    private bool _isProgressRingIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string? _errorMessage;

    public GameInfoViewModel(
        IGameWorkflowService games,
        IInstallDialogService installDialog,
        IFolderPickerService folderPicker,
        IUiDispatcher dispatcher)
    {
        _games = games;
        _installDialog = installDialog;
        _folderPicker = folderPicker;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync(string appName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        Deactivate();
        _appName = appName;
        _active = true;
        _games.GameChanged += OnGameChanged;
        _games.OperationChanged += OnOperationChanged;
        await ApplyGameAsync(_games.GetGame(appName), cancellationToken);
        await ApplyOperationAsync(_games.CurrentOperation, cancellationToken);
    }

    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _games.GameChanged -= OnGameChanged;
        _games.OperationChanged -= OnOperationChanged;
    }

    [RelayCommand]
    private async Task PrimaryActionAsync(CancellationToken cancellationToken)
    {
        if (Game is null)
            return;
        ErrorMessage = null;
        try
        {
            switch (Game.InstallState)
            {
                case InstallState.Installed:
                    await _games.LaunchAsync(Game.AppName, cancellationToken);
                    break;
                case InstallState.NeedUpdate:
                    await EnqueueAsync(ActionType.Update, Game.InstallPath, cancellationToken);
                    break;
                case InstallState.Broken:
                    await EnqueueAsync(ActionType.Repair, Game.InstallPath, cancellationToken);
                    break;
                default:
                    await _installDialog.ShowAsync(Game.AppName, cancellationToken);
                    break;
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Game action failed: {exception.GetType().Name}";
            IsProgressRingVisible = false;
            IsPrimaryActionEnabled = true;
        }
    }

    [RelayCommand]
    private Task UninstallAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(ActionType.Uninstall, Game?.InstallPath, cancellationToken);

    [RelayCommand]
    private Task VerifyRepairAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(ActionType.Repair, Game?.InstallPath, cancellationToken);

    [RelayCommand]
    private async Task ImportGameAsync(CancellationToken cancellationToken)
    {
        if (Game is null)
            return;
        var folder = await _folderPicker.PickFolderAsync(null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(folder))
            await EnqueueAsync(ActionType.Import, folder, cancellationToken);
    }

    [RelayCommand]
    private async Task MoveGameAsync(CancellationToken cancellationToken)
    {
        if (Game?.InstallPath is null)
            return;
        var parent = await _folderPicker.PickFolderAsync(Game.InstallPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(parent))
            return;
        var destination = Path.Combine(parent, Game.AppName);
        var result = await _games.EnqueueAsync(
            new InstallOperationRequest(Game.AppName, ActionType.Move, Game.InstallPath, destination),
            cancellationToken);
        if (!result.IsAccepted)
            ErrorMessage = result.Message;
    }

    private async Task EnqueueAsync(
        ActionType action,
        string? location,
        CancellationToken cancellationToken)
    {
        if (Game is null || string.IsNullOrWhiteSpace(location))
            return;
        var result = await _games.EnqueueAsync(
            new InstallOperationRequest(Game.AppName, action, location),
            cancellationToken);
        if (!result.IsAccepted)
            ErrorMessage = result.Message;
    }

    private void OnGameChanged(object? sender, GamePresentationData game)
    {
        if (game.AppName == _appName)
            _ = ApplyGameAsync(game, CancellationToken.None);
    }

    private void OnOperationChanged(object? sender, InstallOperationSnapshot? operation) =>
        _ = ApplyOperationAsync(operation, CancellationToken.None);

    private async Task ApplyGameAsync(
        GamePresentationData? game,
        CancellationToken cancellationToken)
    {
        if (game is null)
            return;
        await _dispatcher.InvokeAsync(() =>
        {
            Game = game;
            TitleImageUri = game.ImageUri;
            IsProgressRingVisible = false;
            IsPrimaryActionEnabled = true;
            switch (game.InstallState)
            {
                case InstallState.Installed:
                    PrimaryActionButtonText = "Play";
                    PrimaryActionIcon = GameActionIcon.Play;
                    IsInstalled = true;
                    IsImportVisible = false;
                    break;
                case InstallState.NeedUpdate:
                    PrimaryActionButtonText = "Update";
                    PrimaryActionIcon = GameActionIcon.Update;
                    IsInstalled = true;
                    IsImportVisible = false;
                    break;
                case InstallState.Broken:
                    PrimaryActionButtonText = "Repair";
                    PrimaryActionIcon = GameActionIcon.Repair;
                    IsInstalled = true;
                    IsImportVisible = false;
                    break;
                default:
                    PrimaryActionButtonText = "Install";
                    PrimaryActionIcon = GameActionIcon.Install;
                    IsInstalled = false;
                    IsImportVisible = true;
                    break;
            }
        }, cancellationToken);
    }

    private async Task ApplyOperationAsync(
        InstallOperationSnapshot? operation,
        CancellationToken cancellationToken)
    {
        if (operation is null || operation.AppName != _appName)
            return;
        await _dispatcher.InvokeAsync(() =>
        {
            switch (operation.Status)
            {
                case ActionStatus.Processing:
                    IsProgressRingIndeterminate = false;
                    ProgressValue = operation.ProgressPercentage;
                    IsProgressRingVisible = true;
                    IsPrimaryActionEnabled = false;
                    PrimaryActionButtonText = $"{operation.ProgressPercentage}%";
                    PrimaryActionIcon = GameActionIcon.None;
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
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Deactivate();
    }
}
