namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftLauncherProvider(UbisoftAuthentication auth, UbisoftLibrary library) : ILauncherProvider
{
    public const string ProviderId = "ubisoft";
    public string Id => ProviderId;
    public string DisplayName => "Ubisoft Connect";
    public ILauncherAuthentication Authentication => auth;
    public ILauncherLibrary Library => library;
    // Downloads, destructive install operations and store search are not implemented yet.
    public ILauncherInstaller? Installer => null;
    public ILauncherStore? Store => null;
}
