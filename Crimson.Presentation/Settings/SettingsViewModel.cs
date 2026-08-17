using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;

namespace Crimson.Presentation;

public partial class SettingsViewModel : ObservableObject, IActivatable
{
    private readonly ISettingsService _settings;
    private readonly IFolderPickerService _folderPicker;
    private readonly IExternalPathLauncher _pathLauncher;
    private readonly string _logsDirectory;
    private bool _active;

    [ObservableProperty]
    private string _defaultInstallLocation = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(
        ISettingsService settings,
        IFolderPickerService folderPicker,
        IExternalPathLauncher pathLauncher,
        string logsDirectory)
    {
        _settings = settings;
        _folderPicker = folderPicker;
        _pathLauncher = pathLauncher;
        _logsDirectory = logsDirectory;
    }

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_active)
            return;
        _active = true;
        IsBusy = true;
        try
        {
            var settings = await _settings.GetAsync(cancellationToken);
            DefaultInstallLocation = settings.DefaultInstallLocation;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Settings could not be loaded: {exception.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Deactivate() => _active = false;

    [RelayCommand]
    private async Task BrowseAsync(CancellationToken cancellationToken)
    {
        var selected = await _folderPicker.PickFolderAsync(DefaultInstallLocation, cancellationToken);
        if (!string.IsNullOrWhiteSpace(selected))
            DefaultInstallLocation = selected;
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            await _settings.SaveAsync(new AppSettings(DefaultInstallLocation), cancellationToken);
            StatusMessage = "Settings saved";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Settings could not be saved: {exception.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenLogsAsync(CancellationToken cancellationToken) =>
        _pathLauncher.OpenAsync(_logsDirectory, cancellationToken);
}
