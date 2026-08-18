using Crimson.Core;
using Crimson.Models;

namespace Crimson.Presentation;

public sealed record GamePresentationData(
    string AppName,
    string Title,
    Uri? ImageUri,
    string AssetBuildVersion,
    InstallState InstallState,
    string? InstallPath,
    bool IsDlc = false);

public sealed record InstallOperationRequest(
    string AppName,
    ActionType Action,
    string Location,
    string? MoveLocation = null);

public sealed record InstallOperationSnapshot(
    string AppName,
    string Title,
    Uri? ImageUri,
    ActionType Action,
    ActionStatus Status,
    int ProgressPercentage,
    double WrittenSizeMiB,
    double TotalWriteSizeMiB,
    double DownloadSpeedMiB,
    string? StatusMessage = null);

public sealed record DriveSpaceSnapshot(long AvailableBytes, long TotalBytes);

public interface IGameWorkflowService
{
    event EventHandler<GamePresentationData>? GameChanged;

    event EventHandler<InstallOperationSnapshot?>? OperationChanged;

    InstallOperationSnapshot? CurrentOperation { get; }

    GamePresentationData? GetGame(string appName);

    IReadOnlyList<GamePresentationData> GetDlcs(string appName);

    IReadOnlyList<GamePresentationData> GetQueuedGames();

    IReadOnlyList<GamePresentationData> GetHistoryGames();

    Task LaunchAsync(string appName, CancellationToken cancellationToken = default);

    Task<InstallCommandResult> EnqueueAsync(
        InstallOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<InstallCommandResult> CancelAsync(string appName, CancellationToken cancellationToken = default);

    Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default);

    Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default);

    Task<InstallContentSize> GetContentSizeAsync(
        string appName,
        CancellationToken cancellationToken = default);

    Task<DriveSpaceSnapshot?> GetDriveSpaceAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IInstallDialogService
{
    Task ShowAsync(string appName, CancellationToken cancellationToken = default);

    void Close();
}
