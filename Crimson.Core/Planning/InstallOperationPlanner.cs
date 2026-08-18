using System.Collections.Immutable;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.Core;

public sealed record InstallManifestIdentity(
    string BuildVersion,
    string Sha1,
    string Sha256);

public sealed record InstallPlanFile(
    string Path,
    long Size,
    string Sha1);

public sealed record InstallPlanningRequest(
    string OperationId,
    string AppName,
    ActionType Action,
    string InstallRoot,
    InstallManifestIdentity TargetManifest,
    IReadOnlyList<InstallPlanFile> TargetFiles,
    InstallFileSystemProbeResult Destination,
    InstallManifestIdentity? InstalledManifest = null,
    IReadOnlyList<InstallPlanFile>? InstalledFiles = null,
    IReadOnlyCollection<string>? InvalidFiles = null,
    IReadOnlyCollection<string>? VerifiedStagedFiles = null,
    string? MoveDestination = null,
    InstallFileSystemProbeResult? Source = null,
    long RequiredDownloadBytes = 0);

public sealed record InstallOperationPlan(
    int Version,
    string OperationId,
    string AppName,
    ActionType Action,
    string InstallRoot,
    string? MoveDestination,
    InstallManifestIdentity TargetManifest,
    InstallManifestIdentity? InstalledManifest,
    ImmutableArray<InstallPlanFile> PendingStageFiles,
    ImmutableArray<InstallPlanFile> VerifiedStageFiles,
    ImmutableArray<string> RemoveFiles,
    ImmutableArray<InstallPlanFile> VerifyFiles,
    long RequiredDownloadBytes,
    long RequiredStagingBytes);

public enum InstallPlanningFailure
{
    InvalidRequest,
    InvalidDestination,
    UnsupportedFileSystem,
    AtomicRenameUnavailable,
    InsufficientSpace,
    CrossVolumeMove,
    UnsupportedAction
}

public sealed class InstallPlanningException(
    InstallPlanningFailure failure,
    string message) : InvalidOperationException(message)
{
    public InstallPlanningFailure Failure { get; } = failure;
}

public sealed record InstallPlanningResult(
    InstallOperationPlan? Plan,
    InstallPlanningFailure? Failure = null,
    string? Message = null)
{
    public bool IsSuccess => Plan is not null;
}

public static class InstallOperationPlanner
{
    public const int CurrentPlanVersion = 1;

    public static InstallPlanningResult Create(InstallPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.AppName) ||
            !Path.IsPathFullyQualified(request.InstallRoot))
        {
            return Failure(InstallPlanningFailure.InvalidRequest, "Operation identity and an absolute install root are required.");
        }

        var targetFiles = IndexFiles(request.TargetFiles, out var targetError);
        if (targetError is not null)
            return Failure(InstallPlanningFailure.InvalidRequest, targetError);
        var installedFiles = IndexFiles(request.InstalledFiles ?? [], out var installedError);
        if (installedError is not null)
            return Failure(InstallPlanningFailure.InvalidRequest, installedError);

        var operationFiles = request.Action switch
        {
            ActionType.Install => targetFiles.Values.ToImmutableArray(),
            ActionType.Update => ChangedOrAdded(targetFiles, installedFiles),
            ActionType.Repair => SelectPaths(targetFiles, request.InvalidFiles),
            ActionType.Import => ImmutableArray<InstallPlanFile>.Empty,
            ActionType.Uninstall => ImmutableArray<InstallPlanFile>.Empty,
            ActionType.Move => ImmutableArray<InstallPlanFile>.Empty,
            _ => default
        };
        if (operationFiles.IsDefault)
            return Failure(InstallPlanningFailure.UnsupportedAction, $"Unsupported operation action: {request.Action}.");

        var removeFiles = request.Action switch
        {
            ActionType.Update => installedFiles.Keys
                .Where(path => !targetFiles.ContainsKey(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            ActionType.Uninstall => installedFiles.Keys
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            _ => ImmutableArray<string>.Empty
        };
        var verifyFiles = request.Action switch
        {
            ActionType.Import => targetFiles.Values.ToImmutableArray(),
            ActionType.Move => installedFiles.Values.ToImmutableArray(),
            _ => operationFiles
        };

        var verifiedPaths = new HashSet<string>(
            request.VerifiedStagedFiles ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (verifiedPaths.Any(path => !operationFiles.Any(file =>
                string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))))
        {
            return Failure(InstallPlanningFailure.InvalidRequest, "Verified progress contains a file outside the operation plan.");
        }

        var verified = operationFiles
            .Where(file => verifiedPaths.Contains(file.Path))
            .ToImmutableArray();
        var pending = operationFiles
            .Where(file => !verifiedPaths.Contains(file.Path))
            .ToImmutableArray();
        long requiredStagingBytes;
        try
        {
            requiredStagingBytes = pending.Aggregate(0L, (total, file) => checked(total + file.Size));
        }
        catch (OverflowException)
        {
            return Failure(InstallPlanningFailure.InvalidRequest, "Operation staging size exceeds the supported range.");
        }

        var destinationFailure = ValidateDestination(request, requiredStagingBytes);
        if (destinationFailure is not null)
            return destinationFailure;

        var plan = new InstallOperationPlan(
            CurrentPlanVersion,
            request.OperationId,
            request.AppName,
            request.Action,
            Path.GetFullPath(request.InstallRoot),
            request.MoveDestination is null ? null : Path.GetFullPath(request.MoveDestination),
            request.TargetManifest,
            request.InstalledManifest,
            pending,
            verified,
            removeFiles,
            verifyFiles,
            request.RequiredDownloadBytes,
            requiredStagingBytes);
        return new InstallPlanningResult(plan);
    }

    private static InstallPlanningResult? ValidateDestination(
        InstallPlanningRequest request,
        long requiredStagingBytes)
    {
        if (request.Action == ActionType.Import || request.Action == ActionType.Uninstall)
            return null;
        if (!request.Destination.Success)
            return Failure(InstallPlanningFailure.InvalidDestination, "Destination filesystem validation failed.");
        if (request.Destination.Location != InstallFileSystemLocation.Local)
            return Failure(InstallPlanningFailure.UnsupportedFileSystem, "Operations require a local destination filesystem.");
        if (request.Action is ActionType.Install or ActionType.Update or ActionType.Repair &&
            !request.Destination.AtomicRenameSupported)
        {
            return Failure(InstallPlanningFailure.AtomicRenameUnavailable, "Transactional publication requires atomic rename support.");
        }
        if (request.Destination.AvailableBytes is not long availableBytes || availableBytes < requiredStagingBytes)
            return Failure(InstallPlanningFailure.InsufficientSpace, "Destination does not have enough available space.");

        if (request.Action != ActionType.Move)
            return null;
        if (request.MoveDestination is null || !Path.IsPathFullyQualified(request.MoveDestination))
            return Failure(InstallPlanningFailure.InvalidDestination, "Move destination must be an absolute path.");
        if (request.Source?.VolumeIdentity is null || request.Destination.VolumeIdentity is null ||
            !string.Equals(request.Source.VolumeIdentity, request.Destination.VolumeIdentity, StringComparison.Ordinal))
        {
            return Failure(InstallPlanningFailure.CrossVolumeMove, "Cross-volume move is not supported.");
        }
        return null;
    }

    private static Dictionary<string, InstallPlanFile> IndexFiles(
        IReadOnlyList<InstallPlanFile> files,
        out string? error)
    {
        var result = new Dictionary<string, InstallPlanFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Size < 0)
            {
                error = $"File size is negative: {file.Path}.";
                return result;
            }
            try
            {
                var path = ManifestRelativePath.Parse(file.Path).Value;
                if (!result.TryAdd(path, file with { Path = path }))
                {
                    error = $"Duplicate operation path: {file.Path}.";
                    return result;
                }
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
                return result;
            }
        }
        error = null;
        return result;
    }

    private static ImmutableArray<InstallPlanFile> ChangedOrAdded(
        IReadOnlyDictionary<string, InstallPlanFile> target,
        IReadOnlyDictionary<string, InstallPlanFile> installed) =>
        target.Values
            .Where(file => !installed.TryGetValue(file.Path, out var oldFile) ||
                !string.Equals(file.Sha1, oldFile.Sha1, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

    private static ImmutableArray<InstallPlanFile> SelectPaths(
        IReadOnlyDictionary<string, InstallPlanFile> files,
        IReadOnlyCollection<string>? selectedPaths)
    {
        var selected = new HashSet<string>(selectedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        return files.Values.Where(file => selected.Contains(file.Path)).ToImmutableArray();
    }

    private static InstallPlanningResult Failure(InstallPlanningFailure failure, string message) =>
        new(null, failure, message);
}
