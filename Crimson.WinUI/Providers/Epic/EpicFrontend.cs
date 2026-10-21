using System;

namespace Crimson.Providers.Epic;

public sealed class EpicFrontend : ILauncherFrontend
{
    public string ProviderId => EpicLauncherProvider.ProviderId;
    public ILauncherSignInView CreateSignInView() => new EpicLoginPage();
    public Type StorePageType => typeof(EpicStorePage);
}
