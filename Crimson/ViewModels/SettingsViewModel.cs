using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;
        private readonly ILogger _logger;

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

        public SettingsViewModel(SettingsManager settingsManager, ILogger logger)
        {
            _settingsManager = settingsManager;
            _logger = logger;
        }

        private async Task SaveSettingsAsync()
        {
            await _settingsManager.SaveSettings();
        }

        [RelayCommand]
        private void OpenLogsDirectory()
        {
            try
            {
                Directory.CreateDirectory(_settingsManager.LogsDirectory);
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = _settingsManager.LogsDirectory,
                });
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to open logs directory");
            }
        }
    }
}
