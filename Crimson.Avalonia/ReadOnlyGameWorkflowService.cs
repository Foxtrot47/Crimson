using Crimson.Core;
using Crimson.Models;
using Crimson.Presentation;

namespace Crimson.Avalonia;

public sealed class ReadOnlyGameWorkflowService(ILibraryService library) : IGameWorkflowService
{
    public event EventHandler<GamePresentationData>? GameChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<InstallOperationSnapshot?>? OperationChanged
    {
        add { }
        remove { }
    }

    public InstallOperationSnapshot? CurrentOperation => null;

    public GamePresentationData? GetGame(string appName) =>
        library.Snapshot.Games.FirstOrDefault(game => game.AppName == appName) is { } game
            ? Map(game)
            : null;

    public IReadOnlyList<GamePresentationData> GetDlcs(string appName) =>
        library.Snapshot.Games.Where(game => game.IsDlc).Select(Map).ToArray();

    public IReadOnlyList<GamePresentationData> GetQueuedGames() => [];

    public IReadOnlyList<GamePresentationData> GetHistoryGames() => [];

    public Task LaunchAsync(string appName, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Game launch is unavailable in the read-only Avalonia host."));

    public Task<InstallCommandResult> EnqueueAsync(
        InstallOperationRequest request,
        CancellationToken cancellationToken = default) =>
        Rejected();

    public Task<InstallCommandResult> CancelAsync(
        string appName,
        CancellationToken cancellationToken = default) =>
        Rejected();

    public Task<InstallCommandResult> PauseAsync(CancellationToken cancellationToken = default) => Rejected();

    public Task<InstallCommandResult> ResumeAsync(CancellationToken cancellationToken = default) => Rejected();

    public Task<InstallContentSize> GetContentSizeAsync(
        string appName,
        CancellationToken cancellationToken = default) =>
        Task.FromException<InstallContentSize>(
            new NotSupportedException("Install sizing is unavailable in the read-only Avalonia host."));

    public Task<DriveSpaceSnapshot?> GetDriveSpaceAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<DriveSpaceSnapshot?>(null);

    private static Task<InstallCommandResult> Rejected() => Task.FromResult(
        new InstallCommandResult(
            InstallCommandOutcome.Rejected,
            "Install operations are unavailable in the read-only Avalonia host."));

    private static GamePresentationData Map(GameSnapshot game) => new(
        game.AppName,
        game.Title,
        game.ImageUri,
        game.AssetBuildVersion,
        game.InstallState,
        game.InstallPath,
        game.IsDlc);
}

public sealed class UnsupportedInstallDialogService : IInstallDialogService
{
    public Task ShowAsync(string appName, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "Install dialogs are unavailable in the read-only Avalonia host."));

    public void Close()
    {
    }
}
