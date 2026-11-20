namespace Crimson.Providers.Ubisoft;

public sealed class UbisoftLauncherProvider(UbisoftAuthentication auth, UbisoftLibrary library, UbisoftInstallManager? installer = null)
    : ILauncherProvider
{
    public const string ProviderId = "ubisoft";
    public string Id => ProviderId;
    public string DisplayName => "Ubisoft Connect";
    public ILauncherAuthentication Authentication => auth;
    public ILauncherLibrary Library => library;
    public ILauncherInstaller? Installer => installer;
    public ILauncherStore? Store => null;
}
