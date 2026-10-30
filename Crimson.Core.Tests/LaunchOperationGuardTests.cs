using System.Reflection;
using System.Text.Json;using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Crimson.Tests;

public sealed class LaunchOperationGuardTests : IDisposable
{
    private readonly DlcTestFixture _fixture = new();

    [Theory]
    [InlineData("base", ActionStatus.Pending)]
    [InlineData("installed", ActionStatus.Processing)]
    [InlineData("installed", ActionStatus.Paused)]
    [InlineData("installed", ActionStatus.Cancelling)]
    public void CoreLaunchContextRejectsRelatedBaseAndDlcOperations(string appName, ActionStatus status)
    {
        _fixture.Library.TrackOperation(Snapshot(appName, status));

        Assert.Null(LaunchContext());
        var starts = 0;
        Assert.False(_fixture.Library.TryStartGame("base", () => { starts++; return true; }));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task OperationQueuedWhileCredentialsArePendingBlocksFinalProcessStart()
    {
        Assert.NotNull(LaunchContext());
        var credentials = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var launch = StartAfterCredentialsAsync(credentials.Task, () => { starts++; return true; });
        _fixture.SetCurrent("unrelated");
        _fixture.Installer.AddToQueue(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        credentials.SetResult();

        Assert.False(await launch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, starts);
        Assert.Null(LaunchContext());
    }

    [Fact]
    public async Task RealLaunchAppRechecksOperationsAfterDelayedCredentials()
    {
        var repository = new HeldLaunchRepository();
        var errors = new CapturedErrors();
        using var logger = new LoggerConfiguration().WriteTo.Sink(errors).CreateLogger();
        typeof(LibraryManager).GetField("_storeRepository", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Library, repository);
        typeof(LibraryManager).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_fixture.Library, logger);
        var auth = (AuthManager)typeof(LibraryManager).GetField("_authManager", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_fixture.Library)!;
        typeof(AuthManager).GetField("_authenticationStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(auth, AuthenticationStatus.LoggedIn);
        await _fixture.Storage.SaveUserData(new UserData { AccountId = "synthetic-account" });
        // A regression reaches a path-policy rejection before any OS process can start.
        _fixture.BaseGame.LocalAppState!.Executable = "../must-not-start.exe";
        var launch = _fixture.Library.LaunchApp("base");
        await repository.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _fixture.SetCurrent("unrelated");
        _fixture.Installer.AddToQueue(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        repository.Credentials.SetResult("{\"code\":\"synthetic-exchange-code\"}");

        Assert.False(await launch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public void RealEnqueueBlocksLaunchBeforeStatusObserversAndSnapshotPublication()
    {
        _fixture.SetCurrent("unrelated");
        var queued = new InstallItem("installed", ActionType.Repair, _fixture.BasePath);
        var observed = false;
        var starts = 0;
        _fixture.Installer.InstallationStatusChanged += item =>
        {
            if (!ReferenceEquals(item, queued)) return;
            observed = true;
            Assert.Same(queued, _fixture.Installer.GameGameInQueue("installed"));
            Assert.False(_fixture.Library.TryStartGame("base", () => { starts++; return true; }));
        };

        _fixture.Installer.AddToQueue(queued);

        Assert.True(observed);
        Assert.Equal(0, starts);
        Assert.Single(_fixture.Installer.GetQueueOperations());
    }

    [Fact]
    public void CurrentOperationCannotBeQueuedAgainForTheSameApp()
    {
        _fixture.SetCurrent("installed");
        _fixture.Installer.AddToQueue(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        Assert.Empty(_fixture.Installer.GetQueueOperations());
    }

    [Fact]
    public void PredecessorTerminalCannotReleaseTheSuccessorsLaunchGuard()
    {
        _fixture.SetCurrent("unrelated");
        var previous = new InstallItem("installed", ActionType.Install, _fixture.BasePath) { Status = ActionStatus.Processing };
        Publish(previous);
        previous.Status = ActionStatus.Success;
        Publish(previous);
        var next = new InstallItem("installed", ActionType.Repair, _fixture.BasePath);
        _fixture.Installer.AddToQueue(next);

        Publish(previous);

        Assert.NotEqual(previous.OperationId, next.OperationId);
        Assert.False(_fixture.Library.TryStartGame("base", () => true));
        Assert.Null(LaunchContext());
    }

    [Fact]
    public async Task RestoringPendingDlcBlocksLaunchBeforeAnyStatusObserverRuns()
    {
        await _fixture.Cache("base", "base-build", "base.bin");
        await _fixture.Cache("installed", "dlc-build", "addon.bin");
        SavePendingDlc();
        var observed = false;
        _fixture.Installer.InstallationStatusChanged += item =>
        {
            if (item.AppName != "installed") return;
            observed = true;
            Assert.False(_fixture.Library.TryStartGame("base", () => true));
            Assert.Null(LaunchContext());
        };

        await _fixture.Installer.LoadPendingInstalls();

        Assert.True(observed);
        Assert.Equal(ActionStatus.Paused, _fixture.Installer.CurrentInstall!.Status);
        Assert.False(_fixture.Library.TryStartGame("base", () => true));
    }

    [Fact]
    public async Task FailedPendingRestoreRollsBackLaunchReservationAndCurrentOperation()
    {
        SavePendingDlc();
        var errors = new List<OperationSnapshot>();
        _fixture.Installer.OperationError += errors.Add;

        await _fixture.Installer.LoadPendingInstalls();

        Assert.Equal(ActionStatus.Failed, Assert.Single(errors).Status);
        Assert.Null(_fixture.Installer.CurrentInstall);
        Assert.Empty(_fixture.IoTasks());
        Assert.True(_fixture.Library.TryStartGame("base", () => true));
        _fixture.SetCurrent("unrelated");
        _fixture.Installer.AddToQueue(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        Assert.Single(_fixture.Installer.GetQueueOperations());
        Assert.False(_fixture.Library.TryStartGame("base", () => true));
    }

    private void SavePendingDlc() => _fixture.Storage.SaveInstallState(JsonSerializer.Serialize(new
    {
        CurrentInstall = new InstallItem("installed", ActionType.Uninstall, _fixture.BasePath),
        IoQueue = Array.Empty<IoTask>(),
        CompletedChunks = Array.Empty<int>()
    }));

    [Fact]
    public void FailedQueueInsertionDoesNotLeaveAPhantomOperation()
    {
        var snapshot = OperationSnapshot.From(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        Assert.Throws<InvalidOperationException>(() => _fixture.Library.TryQueueOperation(snapshot,
            () => throw new InvalidOperationException("Synthetic queue failure")));
        Assert.True(_fixture.Library.TryStartGame("base", () => true));
    }

    [Theory]
    [InlineData(ActionStatus.Success)]
    [InlineData(ActionStatus.Failed)]
    [InlineData(ActionStatus.Cancelled)]
    public void TerminalOperationsReleaseTheLaunchGuard(ActionStatus terminal)
    {
        _fixture.Library.TrackOperation(Snapshot("installed", ActionStatus.Processing));
        _fixture.Library.TrackOperation(Snapshot("installed", terminal));
        Assert.NotNull(LaunchContext());
        Assert.True(_fixture.Library.TryStartGame("base", () => true));
    }

    [Fact]
    public void UnrelatedOperationDoesNotBlockThisGame()
    {
        _fixture.Library.TrackOperation(Snapshot("unrelated", ActionStatus.Processing));
        Assert.NotNull(LaunchContext());
        Assert.True(_fixture.Library.TryStartGame("base", () => true));
    }

    [Fact]
    public void FinalStartAlsoRechecksAnInstallationRemovedDuringCredentialRetrieval()
    {
        Assert.NotNull(LaunchContext());
        _fixture.BaseGame.LocalAppState!.InstallStatus = InstallState.NotInstalled;
        Assert.False(_fixture.Library.TryStartGame("base", () => throw new InvalidOperationException("Must not start")));
    }

    [Fact]
    public async Task RegisteringAnOperationCannotOverlapTheProcessStartDecision()
    {
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStart = new ManualResetEventSlim();
        var launch = Task.Run(() => _fixture.Library.TryStartGame("base", () =>
        {
            starting.SetResult();
            if (!releaseStart.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return true;
        }));
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _fixture.SetCurrent("unrelated");
        var registration = Task.Run(() =>
        {
            registrationAttempted.SetResult();
            _fixture.Installer.AddToQueue(new InstallItem("installed", ActionType.Repair, _fixture.BasePath));
        });
        try
        {
            await registrationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(registration.IsCompleted);
        }
        finally
        {
            releaseStart.Set();
            await Task.WhenAll(launch, registration).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(LaunchContext());
    }

    private async Task<bool> StartAfterCredentialsAsync(Task credentials, Func<bool> start)
    {
        await credentials;
        return _fixture.Library.TryStartGame("base", start);
    }

    private void Publish(InstallItem item) => typeof(InstallManager)
        .GetMethod("PublishOperation", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_fixture.Installer, [item]);

    private object? LaunchContext() => typeof(LibraryManager)
        .GetMethod("GetLaunchContext", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_fixture.Library, ["base"]);

    private static OperationSnapshot Snapshot(string appName, ActionStatus status) =>
        new(appName, ActionType.Install, status, OperationPhase.Transferring, 0, 0, 0, 0, null);

    private sealed class CapturedErrors : ILogEventSink
    {
        public List<LogEvent> Errors { get; } = [];
        public void Emit(LogEvent entry) { if (entry.Level >= LogEventLevel.Error) Errors.Add(entry); }
    }

    private sealed class HeldLaunchRepository : IStoreRepository
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Credentials { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> GetGameToken() { Requested.TrySetResult(); return Credentials.Task; }
        public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<StoreProductDetails?> FetchStoreProductDetails(string nameSpace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> FetchGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> GetCloudSaveFilesAsync(string appName, bool manifestsOnly, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, CloudSaveFileEntry>?> CreateCloudSaveFilesAsync(string appName, IReadOnlyList<string> files, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> DownloadCloudSaveFileAsync(string readLink, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UploadCloudSaveFileAsync(string writeLink, byte[] data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose() => _fixture.Dispose();
}
