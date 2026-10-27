using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Utils;

namespace Crimson.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsManager _settingsManager;
        private readonly AuthManager _authManager;
        private readonly IFolderLauncher _folderLauncher;
        private readonly Storage? _storage;
        private readonly IArtworkCache? _artworkCache;
        private readonly LibraryManager? _libraryManager;

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

        public CacheCategory ArtworkCache { get; } = new() { Kind = CacheCategoryKind.Artwork, Name = UiStrings.CacheArtwork, CanClear = true };
        public CacheCategory ManifestCache { get; } = new() { Kind = CacheCategoryKind.Manifests, Name = UiStrings.CacheManifests };
        public CacheCategory MetadataCache { get; } = new() { Kind = CacheCategoryKind.Metadata, Name = UiStrings.CacheMetadata };
        public CacheCategory StoreDetailsCache { get; } = new() { Kind = CacheCategoryKind.StoreDetails, Name = UiStrings.CacheStoreDetails, CanClear = true };
        public CacheCategory LibraryCache { get; } = new() { Kind = CacheCategoryKind.Library, Name = UiStrings.Library };

        public IReadOnlyList<CacheCategory> CacheCategories => [ArtworkCache, ManifestCache, MetadataCache, StoreDetailsCache, LibraryCache];

        [ObservableProperty]
        private string _cacheTotalText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanChangeCache))]
        [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
        private bool _isCacheBusy;

        public bool CanChangeCache => !IsCacheBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCacheError))]
        private string _cacheErrorMessage = string.Empty;

        public bool HasCacheError => !string.IsNullOrEmpty(CacheErrorMessage);

        /// <summary>Raised after cache sizes change so the view can redraw the usage bar.</summary>
        public event EventHandler? CacheUsageChanged;

        public SettingsViewModel(
            SettingsManager settingsManager,
            AuthManager authManager,
            IFolderLauncher folderLauncher,
            Storage? storage = null,
            IArtworkCache? artworkCache = null,
            LibraryManager? libraryManager = null)
        {
            _settingsManager = settingsManager;
            _authManager = authManager;
            _folderLauncher = folderLauncher;
            _storage = storage;
            _artworkCache = artworkCache;
            _libraryManager = libraryManager;
        }

        public bool IsCacheUsageAvailable => _storage is not null && _artworkCache is not null && _libraryManager is not null;

        [RelayCommand]
        private async Task LoadCacheUsage()
        {
            if (!IsCacheUsageAvailable) return;
            IsCacheBusy = true;
            try
            {
                await RefreshCacheUsageAsync();
            }
            finally
            {
                IsCacheBusy = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanChangeCache))]
        private async Task ClearCache(CacheCategory? category)
        {
            if (!IsCacheUsageAvailable || category is not { CanClear: true }) return;
            IsCacheBusy = true;
            CacheErrorMessage = string.Empty;
            try
            {
                var cleared = await Task.Run(() =>
                {
                    switch (category.Kind)
                    {
                        case CacheCategoryKind.Artwork:
                            return _artworkCache!.Clear();
                        case CacheCategoryKind.StoreDetails:
                            _libraryManager!.ClearStoreProductDetailsCache();
                            return true;
                        default:
                            return false;
                    }
                });
                if (!cleared) CacheErrorMessage = UiStrings.CacheClearIncomplete;
            }
            catch (Exception)
            {
                CacheErrorMessage = UiStrings.CacheClearIncomplete;
            }
            finally
            {
                await RefreshCacheUsageAsync();
                IsCacheBusy = false;
            }
        }

        [RelayCommand]
        private void OpenCacheDirectory()
        {
            if (_storage is not null) _folderLauncher.Open(_storage.AppDataDirectory);
        }

        private async Task RefreshCacheUsageAsync()
        {
            var (usage, artworkBytes) = await Task.Run(() => (_storage!.GetCacheUsage(), _artworkCache!.GetSizeBytes()));
            foreach (var category in CacheCategories)
            {
                category.Bytes = category.Kind switch
                {
                    CacheCategoryKind.Artwork => artworkBytes,
                    CacheCategoryKind.Manifests => usage.ManifestBytes,
                    CacheCategoryKind.Metadata => usage.MetadataBytes,
                    CacheCategoryKind.StoreDetails => usage.StoreDetailsBytes,
                    _ => usage.LibraryBytes
                };
                category.SizeText = SizeFormatter.Format(category.Bytes);
            }
            CacheTotalText = SizeFormatter.Format(CacheCategories.Sum(category => category.Bytes));
            CacheUsageChanged?.Invoke(this, EventArgs.Empty);
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
