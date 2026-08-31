using System;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Crimson.Controls
{
    public sealed partial class AppInstallDialog : UserControl
    {
        public AppInstallDialogViewModel ViewModel { get; }
        public AppInstallDialog()
        {
            this.InitializeComponent();
            ViewModel = App.GetService<AppInstallDialogViewModel>();
            ViewModel.RequestClose += OnRequestClose;
            ViewModel.FolderPickerRequested += ShowFolderPicker;
        }

        public async Task ShowAsync(Game gameInfo)
        {
            try
            {
                await ViewModel.InitializeAsync(gameInfo);
                await InstallContentDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
            catch (Exception ex)
            {
                App.GetService<ILogger>().Error(ex, "AppInstallDialog: Failed to show install dialog");
            }
        }

        private void OnRequestClose()
        {
            InstallContentDialog?.Hide();
        }

        private async Task<string> ShowFolderPicker()
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();

            // Get the window handle for the current window
            var window = ((App)Application.Current).GetWindow();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // Initialize the folder picker with the window handle
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            // Set folder picker options
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
            folderPicker.FileTypeFilter.Add("*");

            // Show the picker and get the selected folder
            var folder = await folderPicker.PickSingleFolderAsync();

            return folder?.Path;
        }

        public void Cleanup()
        {
            ViewModel.RequestClose -= OnRequestClose;
            ViewModel.FolderPickerRequested -= ShowFolderPicker;
        }
    }
}
