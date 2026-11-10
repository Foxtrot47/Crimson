using System;
using Microsoft.UI.Xaml;

namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftFrontend : ILauncherFrontend
{
    public string ProviderId => UbisoftLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new UbisoftSignInView();
    public Type? StorePageType => null;
    public string? StoreLogo => "ms-appx:///Assets/Providers/ubisoft.png";
}
