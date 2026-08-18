using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Presentation;
using Crimson.PresentationAdapters;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

public sealed partial class GameInfoPage : Page
{
    public GameInfoViewModel ViewModel { get; }
    private readonly WinUiInstallDialogService _installDialogService;

    public GameInfoPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<GameInfoViewModel>();
        _installDialogService = App.GetService<WinUiInstallDialogService>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        InstallDialog.XamlRoot = XamlRoot;
        _installDialogService.Register(ShowInstallDialogAsync, InstallDialog.Hide);
        if (e.Parameter is string appName)
            await ViewModel.LoadAsync(appName);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _installDialogService.Unregister();
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    private Task ShowInstallDialogAsync(string appName, CancellationToken cancellationToken) =>
        InstallDialog.ShowAsync(appName, cancellationToken);
}
