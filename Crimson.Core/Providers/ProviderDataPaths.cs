using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

namespace Crimson.Providers;

public static class ProviderDataPaths
{
    /// <summary>Epic retains its legacy root; other launchers never share its account or install state.</summary>
    public static string ForProvider(string appDataRoot, string providerId)
    {
        GameId.ValidateProviderId(providerId);
        var root = Path.GetFullPath(appDataRoot);
        return providerId == GameId.LegacyProviderId ? root : Path.Combine(root, "providers", providerId);
    }

    // Native IDs may contain slashes, colons, or other filesystem syntax.
    public static string GameFileKey(GameId id) =>
        id.ProviderId + "-" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.LocalId)));
}
