using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Serilog;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;
        private readonly ILogger _logger;
        private readonly IPlatformPathLauncher _pathLauncher;

        public bool MicaEnabled
        {
            get => _settingsManager.MicEnabled;
            set
            {
                _settingsManager.MicEnabled = value;
                OnPropertyChanged();
                _ = SaveSettingsAsync();
            }
        }

        public string DefaultInstallLocation
        {
            get => _settingsManager.DefaultInstallLocation;
            set
            {
                _settingsManager.DefaultInstallLocation = value;
                OnPropertyChanged();
                _ = SaveSettingsAsync();
            }
        }

        public string LogsDirectory => _settingsManager.LogsDirectory;

        [ObservableProperty]
        private bool _advancedSettingsExpanded;

        public SettingsViewModel(
            SettingsManager settingsManager,
            ILogger logger,
            IPlatformPathLauncher pathLauncher)
        {
            _settingsManager = settingsManager;
            _logger = logger;
            _pathLauncher = pathLauncher;
        }

        private async Task SaveSettingsAsync()
        {
            await _settingsManager.SaveSettings();
        }

        [RelayCommand]
        private async Task OpenLogsDirectoryAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _pathLauncher.OpenDirectoryAsync(
                    _settingsManager.LogsDirectory,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to open logs directory");
            }
        }
    }
}
