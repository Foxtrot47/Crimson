using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Crimson.Providers.Epic;

public sealed class EpicFrontend : ILauncherFrontend
{
    public string ProviderId => EpicLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new EpicLoginPage();
    public Type StorePageType => typeof(EpicStorePage);
    public string? StoreLogo => "ms-appx:///Assets/Providers/epic.png";
    public Color? StoreTileColor => Color.FromArgb(255, 255, 255, 255);

    // Importing existing Epic launcher installations belongs to the Epic provider, not the shell.
    public Func<Task>? Import => () =>
        ((App)Application.Current).GetWindow() is Views.MainWindow window
            ? window.ShowEpicImportAsync()
            : Task.CompletedTask;
}
