using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftFrontend : ILauncherFrontend
{
    public string ProviderId => UbisoftLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new UbisoftSignInView();
    public Type? StorePageType => null;
    public string? StoreLogo => "ms-appx:///Assets/Providers/ubisoft.png";
    public Color? StoreTileColor => Color.FromArgb(255, 0, 110, 245);

    public Func<Task>? Import => () =>
        ((App)Application.Current).GetWindow() is Views.MainWindow window
            ? window.ShowGameImportAsync(ProviderId)
            : Task.CompletedTask;
}
