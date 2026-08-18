using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;

namespace Crimson.Tests;

public sealed class LibraryServiceTests
{
    [Fact]
    public async Task ConcurrentRefreshesShareOneRemoteOperationAndPublication()
    {
        var repository = new StubRepository();
        var store = new StubStore();
        using var service = new LibraryService(repository, store);
        var publications = 0;
        service.Changed += (_, _) => publications++;
        repository.BlockAssets();

        var first = service.RefreshAsync(force: true);
        await repository.AssetRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(force: true);
        repository.ReleaseAssets();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, repository.AssetRequests);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(1, publications);
        Assert.Equal(results[0].Snapshot, results[1].Snapshot);
        Assert.Equal(1, results[0].Snapshot.Sequence);
    }

    [Fact]
    public async Task FailedRefreshPreservesPreviousSnapshotAndTimestamp()
    {
        var existingGame = Game("game", "Existing", "1.0");
        var store = new StubStore(State(existingGame));
        var repository = new StubRepository
        {
            AssetFailure = new RepositoryFailure(
                RepositoryFailureKind.Network,
                "network unavailable")
        };
        using var service = new LibraryService(repository, store);
        var previous = service.Snapshot;

        var result = await service.RefreshAsync(force: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(LibraryRefreshFailureKind.Repository, result.Failure?.Kind);
        Assert.Equal(RepositoryFailureKind.Network, result.Failure?.RepositoryKind);
        Assert.Same(previous, result.Snapshot);
        Assert.Equal(DateTimeOffset.MinValue, result.Snapshot.RefreshedAt);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task SuccessfulRefreshPublishesImmutableMetadataSnapshot()
    {
        var repository = new StubRepository();
        var store = new StubStore();
        using var service = new LibraryService(repository, store);

        var result = await service.RefreshAsync(force: true);
        store.State.Games["game"].AppTitle = "Mutated after publication";

        Assert.True(result.IsSuccess);
        var snapshot = Assert.Single(result.Snapshot.Games);
        Assert.Equal("Game", snapshot.Title);
        Assert.Equal("2.0", snapshot.AssetBuildVersion);
        Assert.True(result.Snapshot.RefreshedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task RefreshDoesNotOverwriteInstallationCommittedDuringFetch()
    {
        var repository = new StubRepository();
        var store = new StubStore
        {
            InstallBeforeWrite = Installed("game", "2.0", new string('a', 40))
        };
        using var service = new LibraryService(repository, store);

        var result = await service.RefreshAsync(force: true);

        Assert.Equal(InstallState.Installed, store.State.LocalInstallations["game"].InstallStatus);
        Assert.Equal(InstallState.Installed, Assert.Single(result.Snapshot.Games).InstallState);
    }

    [Fact]
    public async Task ManifestDigestPreventsFalseRocketLeagueStyleUpdate()
    {
        var digest = new string('a', 40);
        var local = Installed("game", "asset-1", digest);
        var store = new StubStore(State(Game("game", "Old", "asset-1"), local));
        var repository = new StubRepository
        {
            ManifestDigest = digest.ToUpperInvariant()
        };
        using var service = new LibraryService(repository, store);

        var result = await service.RefreshAsync(force: true);

        var game = Assert.Single(result.Snapshot.Games);
        Assert.Equal("2.0", game.AssetBuildVersion);
        Assert.Equal(GameUpdateClassification.Current, game.UpdateClassification);
        Assert.Equal(InstallState.Installed, store.State.LocalInstallations["game"].InstallStatus);
    }

    [Fact]
    public async Task ChangedManifestDigestPersistsUpdateClassification()
    {
        var local = Installed("game", "1.0", new string('b', 40));
        var store = new StubStore(State(Game("game", "Old", "1.0"), local));
        var repository = new StubRepository
        {
            ManifestDigest = new string('c', 40)
        };
        using var service = new LibraryService(repository, store);

        var result = await service.RefreshAsync(force: true);

        var game = Assert.Single(result.Snapshot.Games);
        Assert.Equal(GameUpdateClassification.UpdateAvailable, game.UpdateClassification);
        var persisted = store.State.LocalInstallations["game"];
        Assert.Equal(InstallState.NeedUpdate, persisted.InstallStatus);
        Assert.Equal(new string('c', 40), persisted.AvailableManifestDigest);
    }

    [Fact]
    public void LaunchPlannerBuildsOrderedUnquotedArguments()
    {
        var game = new GameSnapshot(
            "game",
            "Game",
            null,
            "sandbox",
            "catalog",
            "1.0",
            "digest",
            "1.0",
            null,
            null,
            InstallState.Installed,
            GameUpdateClassification.Current,
            Path.Combine(Path.GetTempPath(), "game"),
            "Binaries/Game.exe");
        var planner = new EpicLaunchPlanner();

        var plan = planner.Create(
            game,
            new LaunchCredentials("exchange", "account", "Player Name"),
            new RuntimeProfile("Windows"));

        Assert.Equal(
            [
                "-AUTH_LOGIN=unused",
                "-AUTH_PASSWORD=exchange",
                "-AUTH_TYPE=exchangecode",
                "-epicapp=game",
                "-epicenv=Prod",
                "-EpicPortal",
                "-epicusername=Player Name",
                "-epicuserid=account",
                "-epicsandboxid=sandbox",
                "-epiclocale=en"
            ],
            plan.Arguments.ToArray());
        Assert.Empty(plan.Environment);
    }

    private static LibraryStoreState State(Game? game = null, LocalAppState? local = null)
    {
        var games = game is null
            ? new Dictionary<string, Game>(StringComparer.Ordinal)
            : new Dictionary<string, Game>(StringComparer.Ordinal) { [game.AppName] = game };
        var installations = local is null
            ? new Dictionary<string, LocalAppState>(StringComparer.Ordinal)
            : new Dictionary<string, LocalAppState>(StringComparer.Ordinal) { [local.AppName] = local };
        return new LibraryStoreState([], games, installations);
    }

    private static Game Game(string appName, string title, string buildVersion) => new()
    {
        AppName = appName,
        AppTitle = title,
        AssetInfos = new AssetInfos
        {
            Windows = Asset(appName, buildVersion)
        },
        Metadata = Metadata(title)
    };

    private static Asset Asset(string appName, string buildVersion) => new()
    {
        AppName = appName,
        BuildVersion = buildVersion,
        CatalogItemId = "catalog",
        Namespace = "namespace"
    };

    private static Metadata Metadata(string title) => new()
    {
        Id = "catalog",
        Namespace = "namespace",
        Title = title,
        KeyImages = []
    };

    private static LocalAppState Installed(
        string appName,
        string buildVersion,
        string digest) => new()
    {
        AppName = appName,
        InstallStatus = InstallState.Installed,
        InstallPath = "games",
        Executable = "game.exe",
        Version = buildVersion,
        InstalledManifestBuildVersion = buildVersion,
        InstalledManifestSha1 = digest
    };

    private sealed class StubStore : ILibraryStore
    {
        public StubStore(LibraryStoreState? state = null)
        {
            State = state ?? LibraryServiceTests.State();
        }

        public LibraryStoreState State { get; private set; }

        public int WriteCount { get; private set; }
        public LocalAppState? InstallBeforeWrite { get; init; }

        public LibraryStoreState Read() => State;

        public LibraryStoreState WriteRefresh(
            IReadOnlyList<Asset> assets,
            IReadOnlyList<Game> games,
            IReadOnlyList<LibraryInstallationRefresh> installationRefreshes)
        {
            WriteCount++;
            if (InstallBeforeWrite is not null)
            {
                State = State with
                {
                    LocalInstallations = new Dictionary<string, LocalAppState>(StringComparer.Ordinal)
                    {
                        [InstallBeforeWrite.AppName] = InstallBeforeWrite
                    }
                };
            }
            var localInstallations = State.LocalInstallations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            foreach (var refresh in installationRefreshes)
            {
                var local = localInstallations[refresh.AppName];
                local.AvailableManifestDigest = refresh.AvailableManifestDigest;
                local.InstallStatus = GameUpdateClassifier.Classify(local, refresh.AssetBuildVersion) ==
                    GameUpdateClassification.UpdateAvailable
                    ? InstallState.NeedUpdate
                    : InstallState.Installed;
            }
            State = new LibraryStoreState(
                assets,
                games.ToDictionary(game => game.AppName, StringComparer.Ordinal),
                localInstallations);
            return State;
        }
    }

    private sealed class StubRepository : IStoreRepository
    {
        private TaskCompletionSource? _assetRelease;

        public int AssetRequests { get; private set; }

        public TaskCompletionSource AssetRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RepositoryFailure? AssetFailure { get; init; }

        public string ManifestDigest { get; init; } = "manifest-digest";

        public void BlockAssets() => _assetRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAssets() => _assetRelease?.TrySetResult();

        public async Task<RepositoryResult<IReadOnlyList<Asset>>> FetchGameAssets(
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default)
        {
            AssetRequests++;
            AssetRequestStarted.TrySetResult();
            if (_assetRelease is not null)
                await _assetRelease.Task.WaitAsync(cancellationToken);
            return AssetFailure is null
                ? RepositoryResult<IReadOnlyList<Asset>>.Success([Asset("game", "2.0")])
                : RepositoryResult<IReadOnlyList<Asset>>.Failed(AssetFailure);
        }

        public Task<RepositoryResult<Metadata>> FetchGameMetaData(
            string nameSpace,
            string catalogItemId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<Metadata>.Success(Metadata("Game")));

        public Task<RepositoryResult<GetManifestUrlData>> GetManifestUrls(
            string nameSpace,
            string catalogItem,
            string appName,
            EpicPayloadPlatform platform,
            string label = "Live",
            CancellationToken cancellationToken = default) => Task.FromResult(
            RepositoryResult<GetManifestUrlData>.Success(new GetManifestUrlData
            {
                BaseUrls = [],
                ManifestUrls = [],
                ManifestHash = ManifestDigest
            }));

        public Task<RepositoryResult<byte[]>> GetGameManifest(
            GetManifestUrlData urlData,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<long>> DownloadFileAsync(
            string url,
            string destinationPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryResult<string>> GetGameToken(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
