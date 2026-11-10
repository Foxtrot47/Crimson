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

    /// <summary>Equal game titles from different stores must not share a default installation.</summary>
    public static string SuggestedInstallPath(string root, GameId id, string title)
    {
        var name = new string(title.Select(character => char.IsControl(character) ||
            "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = GameFileKey(id);
        return Path.Combine(Path.GetFullPath(root), id.ProviderId, name);
    }
}
