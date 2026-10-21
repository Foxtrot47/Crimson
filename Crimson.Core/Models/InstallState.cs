namespace Crimson.Models;

public enum InstallState
{
    NotInstalled,
    Installing,
    InstallationPaused,
    Installed,
    Playing,
    Broken,
    Repairing,
    NeedUpdate,
    Updating,
    UpdatingPaused
}
