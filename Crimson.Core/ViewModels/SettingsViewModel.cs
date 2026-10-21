using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Providers;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;
        private readonly LauncherAuthentication _authManager;
        private readonly IFolderLauncher _folderLauncher;

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
        public System.Collections.Generic.IReadOnlyList<LauncherAccount> Accounts => _authManager.Accounts;

        [ObservableProperty]
        private bool _advancedSettingsExpanded;

        public SettingsViewModel(
            SettingsManager settingsManager,
            LauncherAuthentication authManager,
            IFolderLauncher folderLauncher)
        {
            _settingsManager = settingsManager;
            _authManager = authManager;
            _folderLauncher = folderLauncher;
        }

        [RelayCommand]
        private async Task DisconnectAccount(LauncherAccount account)
        {
            await _authManager.Logout(account.ProviderId);
        }

        [RelayCommand]
        private void ConnectAccount(LauncherAccount account) => _authManager.RequestSignIn(account.ProviderId);

        private async Task SaveSettingsAsync()
        {
            await _settingsManager.SaveSettings();
        }

        [RelayCommand]
        private void OpenLogsDirectory()
        {
            _folderLauncher.Open(_settingsManager.LogsDirectory);
        }
    }
}
