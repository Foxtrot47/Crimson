using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Models;

namespace Crimson.Presentation;

public sealed record DownloadQueueItemViewModel(
    string AppName,
    string Title,
    Uri? ImageUri,
    InstallState InstallState);

public partial class CurrentOperationViewModel : ObservableObject, IActivatable
{
    private readonly IGameWorkflowService _games;
    private readonly IUiDispatcher _dispatcher;
    private int _activationCount;
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private Uri? _imageUri;

    [ObservableProperty]
    private string _actionText = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private int _progressPercentage;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private bool _canResume;

    [ObservableProperty]
    private bool _canCancel;

    public CurrentOperationViewModel(IGameWorkflowService games, IUiDispatcher dispatcher)
    {
        _games = games;
        _dispatcher = dispatcher;
    }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_activationCount++ > 0)
            return;
        _games.OperationChanged += OnOperationChanged;
        await ApplyAsync(_games.CurrentOperation, cancellationToken);
    }

    public void Deactivate()
    {
        if (_activationCount == 0 || --_activationCount > 0)
            return;
        _games.OperationChanged -= OnOperationChanged;
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken cancellationToken) =>
        _ = await _games.PauseAsync(cancellationToken);

    [RelayCommand]
    private async Task ResumeAsync(CancellationToken cancellationToken) =>
        _ = await _games.ResumeAsync(cancellationToken);

    [RelayCommand]
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        if (_games.CurrentOperation is { } operation)
            _ = await _games.CancelAsync(operation.AppName, cancellationToken);
    }

    private void OnOperationChanged(object? sender, InstallOperationSnapshot? operation) =>
        _ = ApplyAsync(operation, CancellationToken.None);

    private async Task ApplyAsync(
        InstallOperationSnapshot? operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.InvokeAsync(() => Apply(operation), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Apply(InstallOperationSnapshot? operation)
    {
        if (operation is null)
        {
            IsVisible = false;
            CanPause = false;
            CanResume = false;
            CanCancel = false;
            return;
        }

        IsVisible = operation.Status is not (
            ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled);
        Title = operation.Title;
        ImageUri = operation.ImageUri;
        ProgressPercentage = operation.ProgressPercentage;
        IsIndeterminate = operation.Status is ActionStatus.Pending or ActionStatus.Cancelling;
        CanPause = operation.Status == ActionStatus.Processing;
        CanResume = operation.Status == ActionStatus.Paused;
        CanCancel = operation.Status is ActionStatus.Pending or ActionStatus.Processing or ActionStatus.Paused;
        ActionText = operation.Status switch
        {
            ActionStatus.Paused => "Paused",
            ActionStatus.Cancelling => "Cancelling",
            ActionStatus.Success => $"{ActionLabel(operation.Action)} completed",
            ActionStatus.Failed => $"{ActionLabel(operation.Action)} failed",
            ActionStatus.Cancelled => $"{ActionLabel(operation.Action)} cancelled",
            _ => $"{ActionLabel(operation.Action)}ing"
        };
        SizeText = operation.Status is ActionStatus.Cancelling
            ? string.Empty
            : $"{FormatSize(operation.WrittenSizeMiB)} of {FormatSize(operation.TotalWriteSizeMiB)}";
        SpeedText = operation.Status == ActionStatus.Processing
            ? $"{operation.DownloadSpeedMiB:0.##} MiB/s"
            : operation.StatusMessage ?? string.Empty;
    }

    private static string ActionLabel(ActionType action) => action switch
    {
        ActionType.Install => "Install",
        ActionType.Update => "Update",
        ActionType.Repair => "Repair",
        ActionType.Uninstall => "Uninstall",
        ActionType.Import => "Import",
        ActionType.Move => "Move",
        ActionType.Verify => "Verify",
        _ => action.ToString()
    };

    private static string FormatSize(double mebibytes) => mebibytes >= 1024
        ? $"{mebibytes / 1024:0.##} GiB"
        : $"{mebibytes:0.##} MiB";
}
