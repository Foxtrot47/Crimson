using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;
        private readonly AuthManager _authManager;
        private readonly IFolderLauncher _folderLauncher;

        public bool AcrylicEnabled
        {
            get => _settingsManager.AcrylicEnabled;
            set
            {
                if (!CanChangeAcrylic || _settingsManager.AcrylicEnabled == value) return;
                var previous = _settingsManager.AcrylicEnabled;
                IsSavingAcrylic = true;
                AcrylicErrorMessage = string.Empty;
                _settingsManager.AcrylicEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAdjustAcrylicOpacity));
                _ = SaveAcrylicAsync(previous);
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanChangeAcrylic))]
        [NotifyPropertyChangedFor(nameof(CanAdjustAcrylicOpacity))]
        private bool _isSavingAcrylic;

        public bool CanChangeAcrylic => !IsSavingAcrylic;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAcrylicError))]
        private string _acrylicErrorMessage = string.Empty;

        public bool HasAcrylicError => !string.IsNullOrEmpty(AcrylicErrorMessage);

        private async Task SaveAcrylicAsync(bool previous)
        {
            try
            {
                await _settingsManager.SaveSettings();
            }
            catch (Exception)
            {
                _settingsManager.AcrylicEnabled = previous;
                OnPropertyChanged(nameof(AcrylicEnabled));
                OnPropertyChanged(nameof(CanAdjustAcrylicOpacity));
                AcrylicErrorMessage = UiStrings.AcrylicSaveFailed;
            }
            finally
            {
                IsSavingAcrylic = false;
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

        public double AcrylicOpacity
        {
            get => _settingsManager.AcrylicOpacity;
            set
            {
                if (_settingsManager.AcrylicOpacity == value) return;
                _settingsManager.AcrylicOpacity = value;
                AcrylicErrorMessage = string.Empty;
                OnPropertyChanged();
                _ = SaveSettingsAsync();
            }
        }

        public bool CanAdjustAcrylicOpacity => AcrylicEnabled && !IsSavingAcrylic;

        public string LogsDirectory => _settingsManager.LogsDirectory;

        public IReadOnlyList<LanguageOption> Languages { get; } = AppLanguage.GetOptions();

        public LanguageOption SelectedLanguage => Languages.First(option =>
            option.Code == (_settingsManager.Language ?? string.Empty));

        public bool IsLanguageRestartRequired => _settingsManager.Language != _settingsManager.StartupLanguage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLanguageError))]
        private string _languageErrorMessage = string.Empty;

        public bool HasLanguageError => !string.IsNullOrEmpty(LanguageErrorMessage);

        [RelayCommand]
        private async Task ChangeLanguage(LanguageOption? language)
        {
            if (language is null || !Languages.Contains(language) || language == SelectedLanguage) return;
            var previous = _settingsManager.Language;
            _settingsManager.Language = language.Code;
            LanguageErrorMessage = string.Empty;
            try
            {
                await _settingsManager.SaveSettings();
            }
            catch (Exception)
            {
                _settingsManager.Language = previous;
                LanguageErrorMessage = UiStrings.LanguageSaveFailed;
            }
            OnPropertyChanged(nameof(SelectedLanguage));
            OnPropertyChanged(nameof(IsLanguageRestartRequired));
        }

        [ObservableProperty]
        private bool _advancedSettingsExpanded;

        public SettingsViewModel(
            SettingsManager settingsManager,
            AuthManager authManager,
            IFolderLauncher folderLauncher)
        {
            _settingsManager = settingsManager;
            _authManager = authManager;
            _folderLauncher = folderLauncher;
        }

        [RelayCommand]
        private async Task Logout()
        {
            await _authManager.Logout();
        }

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
