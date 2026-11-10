using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Crimson.Resources;
using Microsoft.UI.Xaml;

namespace Crimson.Providers.Epic;

public sealed class EpicFrontend : ILauncherFrontend
{
    public string ProviderId => EpicLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new EpicLoginPage();
    public Type StorePageType => typeof(EpicStorePage);
    public string? StoreLogo => "ms-appx:///Assets/Providers/epic.png";

    // Importing existing Epic launcher installations belongs to the Epic provider, not the shell.
    public IReadOnlyList<LauncherLibraryAction> LibraryActions =>
    [
        new LauncherLibraryAction(UiStrings.ImportFromEpic, "\uE8B5", () =>
            ((App)Application.Current).GetWindow() is Views.MainWindow window
                ? window.ShowEpicImportAsync()
                : Task.CompletedTask)
    ];
}
