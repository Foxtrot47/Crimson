using System;
using Crimson.Providers;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;

namespace Crimson.Views
{
    /// <summary>
    /// Settings Page
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

        public SettingsPage()
        {
            InitializeComponent();
            DataContext = App.GetService<SettingsViewModel>();
            foreach (var account in ViewModel.Accounts)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new TextBlock { Text = account.DisplayName, VerticalAlignment = VerticalAlignment.Center });
                var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                status.SetBinding(TextBlock.TextProperty, new Binding { Source = account, Path = new PropertyPath(nameof(LauncherAccount.StatusText)), Mode = BindingMode.OneWay });
                row.Children.Add(status);
                var connect = new Button { Content = "Connect", Command = ViewModel.ConnectAccountCommand, CommandParameter = account };
                connect.SetBinding(Button.IsEnabledProperty, new Binding { Source = account, Path = new PropertyPath(nameof(LauncherAccount.CanConnect)), Mode = BindingMode.OneWay });
                var disconnect = new Button { Content = "Disconnect", Command = ViewModel.DisconnectAccountCommand, CommandParameter = account };
                disconnect.SetBinding(Button.IsEnabledProperty, new Binding { Source = account, Path = new PropertyPath(nameof(LauncherAccount.IsConnected)), Mode = BindingMode.OneWay });
                row.Children.Add(connect);
                row.Children.Add(disconnect);
                if (App.GetService<LauncherFrontendRegistry>().Get(account.ProviderId) is ILauncherConfigurationFrontend configuration)
                {
                    var configure = new Button { Content = "Configure" };
                    configure.Click += async (_, _) =>
                    {
                        var dialog = new ContentDialog
                        {
                            Title = account.DisplayName,
                            Content = configuration.CreateConfigurationView(),
                            CloseButtonText = "Close",
                            XamlRoot = XamlRoot
                        };
                        await dialog.ShowAsync();
                    };
                    row.Children.Add(configure);
                }
                AccountList.Children.Add(row);
            }
        }
    }
}
