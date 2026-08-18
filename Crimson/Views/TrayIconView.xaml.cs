using Crimson.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views;

public sealed partial class TrayIconView : UserControl
{
    public TrayIconView()
    {
        InitializeComponent();
        ViewModel = App.GetService<TrayViewModel>();
        Unloaded += OnUnloaded;
    }

    public TrayViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs args) => TrayIcon.Dispose();
}
