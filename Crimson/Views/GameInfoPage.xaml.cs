using System.Threading.Tasks;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

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
        // Initialize the dialog
        InstallDialog.XamlRoot = this.XamlRoot;
        await InstallDialog.ShowAsync(ViewModel.Game);
    }
}
