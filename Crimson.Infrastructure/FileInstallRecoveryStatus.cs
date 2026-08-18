using Crimson.Core;
using Crimson.Utils;

namespace Crimson.Infrastructure;

public sealed class FileInstallRecoveryStatus : IInstallRecoveryStatus
{
    public bool HasUnresolvedTransaction(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return false;
        var journalPath = ManifestPath.ResolveUnderRoot(
            installRoot,
            ManifestRelativePath.Parse(".Crimson/update-transaction.json"));
        return File.Exists(journalPath);
    }
}
