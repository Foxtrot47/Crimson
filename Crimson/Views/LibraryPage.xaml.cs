using Crimson.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
namespace Crimson.Views;

/// <summary>
/// Library Page which shows list of currently installed games
/// </summary>
public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    public LibraryPage()
    {
        InitializeComponent();
        DataContext = App.GetService<LibraryViewModel>();
    }

    private void GameButton_Click(object sender, RoutedEventArgs e)
    {
        var clickedButton = (Button)sender;
        var game = (LibraryItemViewModel)clickedButton.DataContext;
        App.GetService<INavigationService>().Navigate(new GameRoute(game.AppName));
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.ActivateAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }
}

