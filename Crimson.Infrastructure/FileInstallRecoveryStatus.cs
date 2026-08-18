using Crimson.Core;
using Crimson.Utils;

namespace Crimson.Infrastructure;

public sealed class FileInstallRecoveryStatus : IInstallRecoveryStatus
{
    public bool HasUnresolvedTransaction(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return false;
        var metadataRoot = ManifestPath.ResolveUnderRoot(
            installRoot,
            ManifestRelativePath.Parse(".Crimson"));
        var updateJournal = ManifestPath.ResolveUnderRoot(
            installRoot,
            ManifestRelativePath.Parse(".Crimson/update-transaction.json"));
        if (File.Exists(updateJournal))
            return true;

        var operationsRoot = Path.Combine(metadataRoot, "operations");
        try
        {
            return Directory.Exists(operationsRoot) &&
                Directory.EnumerateFiles(operationsRoot, "journal.json", SearchOption.AllDirectories).Any();
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
