using System;
using System.Threading.Tasks;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Crimson.Views;

public sealed partial class GameInfoPage : Page
{
    public GameInfoViewModel ViewModel { get; }

    public GameInfoPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<GameInfoViewModel>();
        DataContext = ViewModel;

        // Subscribe to dialog and picker events
        ViewModel.ShowInstallDialogRequested += ShowInstallDialog;
        ViewModel.CloseInstallDialogRequested += CloseInstallDialog;
        ViewModel.FolderPickerRequested += HandleFolderPickerRequest;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await ViewModel.OnNavigatedTo(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async Task ShowInstallDialog()
    {
        await ConfirmInstallDialog.ShowAsync(ContentDialogPlacement.Popup);
    }

    private void CloseInstallDialog()
    {
        ConfirmInstallDialog.Hide();
    }

    /// <summary>
    ///  Handles the Click event of the InstallLocationChangeButton control.
    /// </summary>
    private async Task<string> HandleFolderPickerRequest()
    {
        // Create a folder picker
        var openPicker = new FolderPicker();

        var window = ((App)Application.Current).GetWindow();
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // Initialize the folder picker with the window handle (HWND).
        InitializeWithWindow.Initialize(openPicker, hWnd);

        // Set options for your folder picker
        openPicker.SuggestedStartLocation = PickerLocationId.Desktop;
        openPicker.FileTypeFilter.Add("*");

        // Open the picker for the user to pick a folder
        var folder = await openPicker.PickSingleFolderAsync();
        if (folder == null) return null;
        return folder.Path;

    }
}
