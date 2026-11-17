using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Crimson.Resources;
using Microsoft.UI.Xaml;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftFrontend : ILauncherFrontend
{
    public string ProviderId => UbisoftLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new UbisoftSignInView();
    public Type? StorePageType => null;
    public string? StoreLogo => "ms-appx:///Assets/Providers/ubisoft.png";

    public IReadOnlyList<LauncherLibraryAction> LibraryActions =>
    [
        new LauncherLibraryAction(UiStrings.ImportFromUbisoft, "\uE8B5", () =>
            ((App)Application.Current).GetWindow() is Views.MainWindow window
                ? window.ShowGameImportAsync(ProviderId)
                : Task.CompletedTask)
    ];
}
