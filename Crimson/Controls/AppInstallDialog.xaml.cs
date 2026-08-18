using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Presentation;
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
        }

        public async Task ShowAsync(string appName, CancellationToken cancellationToken = default)
        {
            try
            {
                await ViewModel.LoadAsync(appName, cancellationToken);
                await InstallContentDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
            catch (Exception ex)
            {
                App.GetService<ILogger>().Error(ex, "AppInstallDialog: Failed to show install dialog");
            }
        }

        public void Hide() => InstallContentDialog?.Hide();
    }
}
