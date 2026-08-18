using Crimson.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views;

public sealed partial class CurrentDownloadControl : UserControl
{
    public CurrentDownloadControl()
    {
        InitializeComponent();
        ViewModel = App.GetService<CurrentOperationViewModel>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public CurrentOperationViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs args) =>
        await ViewModel.ActivateAsync();

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        ViewModel.Deactivate();
}
