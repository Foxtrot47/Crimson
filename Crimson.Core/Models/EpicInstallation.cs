namespace Crimson.Models;

public sealed record EpicInstallation(
    string RecordName,
    string AppName,
    string CatalogNamespace,
    string CatalogItemId,
    string InstallLocation,
    string InstallationGuid,
    string BuildVersion,
    string[]? InstallTags = null);
