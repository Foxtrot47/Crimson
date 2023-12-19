using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using System.Diagnostics;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;

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

        public SettingsViewModel(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager;
        }

        private async Task SaveSettingsAsync()
        {
            await _settingsManager.SaveSettings();
        }

        [RelayCommand]
        private void OpenLogsDirectory()
        {
            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = _settingsManager.LogsDirectory,
            });
        }
    }
}
