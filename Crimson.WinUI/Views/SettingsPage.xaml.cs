using Crimson.ViewModels;
using Crimson.Core;
using Crimson.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading.Tasks;
using Serilog;

namespace Crimson.Views
{
    /// <summary>
    /// Settings Page
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;
        private bool _restartDialogOpen;

        private async void LanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not SettingsViewModel viewModel ||
                LanguagePicker.SelectedItem is not LanguageOption language || language == viewModel.SelectedLanguage) return;
            LanguagePicker.IsEnabled = false;
            try
            {
                await viewModel.ChangeLanguageCommand.ExecuteAsync(language);
                if (viewModel.IsLanguageRestartRequired && !viewModel.HasLanguageError)
                    await ShowLanguageRestartDialogAsync();
            }
            finally
            {
                LanguagePicker.IsEnabled = true;
            }
        }

        private async void RestartClicked(object sender, RoutedEventArgs e) =>
            await ShowLanguageRestartDialogAsync();

        private static bool HasPendingOperations()
        {
            var installer = App.GetService<InstallManager>();
            return installer.CurrentOperation is { IsActive: true } || installer.GetQueueOperations().Count > 0;
        }

        private async Task ShowLanguageRestartDialogAsync()
        {
            if (_restartDialogOpen || !IsLoaded || !ViewModel.IsLanguageRestartRequired) return;
            _restartDialogOpen = true;
            try
            {
                var result = await App.GetService<DialogCoordinator>().ShowAsync(ShowRestartPromptAsync);
                if (result != ContentDialogResult.Primary || !IsLoaded) return;
                if (HasPendingOperations())
                {
                    ViewModel.LanguageErrorMessage = UiStrings.LanguageRestartBusy;
                    return;
                }
                // Restart terminates the app, bypassing the close-to-tray behavior.
                // A successful request does not return to this process.
                var failure = AppInstance.Restart(string.Empty);
                Log.Warning("Application restart request returned: {Reason}", failure);
                ViewModel.LanguageErrorMessage = UiStrings.LanguageRestartFailed;
            }
            catch (Exception exception)
            {
                Log.Warning("Language restart dialog or request failed: {ErrorType}", exception.GetType().Name);
                ViewModel.LanguageErrorMessage = UiStrings.LanguageRestartFailed;
            }
            finally
            {
                _restartDialogOpen = false;
            }
        }

        private async Task<ContentDialogResult> ShowRestartPromptAsync()
        {
            if (!IsLoaded || !ViewModel.IsLanguageRestartRequired) return ContentDialogResult.None;
            var busy = HasPendingOperations();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = UiStrings.RestartCrimson,
                Content = busy ? UiStrings.LanguageRestartBusy : UiStrings.LanguageRestartPrompt,
                PrimaryButtonText = UiStrings.RestartCrimson,
                CloseButtonText = UiStrings.Later,
                IsPrimaryButtonEnabled = !busy,
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync();
        }

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = App.GetService<SettingsViewModel>();
        }
    }
}
