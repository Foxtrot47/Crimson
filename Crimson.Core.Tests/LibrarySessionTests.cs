using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class LibrarySessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-library-session-{Guid.NewGuid():N}");
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly HttpClient _http = new();

    [Fact]
    public async Task LogoutDiscardsOldMetadataAndForcesNewAccountRefresh()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository();
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);
        var published = new List<string>();
        library.LibraryUpdated += games => published.AddRange(games.Select(game => game.AppName));

        var first = library.GetLibraryData();
        await repository.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await auth.Logout();
        repository.Account = "second";
        var second = library.GetLibraryData();
        repository.FirstMetadata.SetResult(ControlledRepository.Metadata("first"));

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        var games = (await second.WaitAsync(TimeSpan.FromSeconds(5))).ToList();
        Assert.Equal(["second"], games.Select(game => game.AppName));
        Assert.DoesNotContain("first", published);
        Assert.False(File.Exists(Path.Combine(_root, "metadata", "first.json")));
        Assert.DoesNotContain("first", storage.GameMetaDataDictionary.Keys);
        Assert.Equal(2, repository.AssetRequests);
    }

    [Fact]
    public async Task AuthenticationFailureDiscardsInFlightRefresh()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository();
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);
        var published = new List<string>();
        library.LibraryUpdated += games => published.AddRange(games.Select(game => game.AppName));

        var refresh = library.GetLibraryData();
        await repository.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
        repository.FirstMetadata.SetResult(ControlledRepository.Metadata("first"));

        Assert.Empty(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(published);
        Assert.False(File.Exists(Path.Combine(_root, "metadata", "first.json")));
    }

    [Fact]
    public async Task LogoutAfterCompletedRefreshClearsCachedOwnership()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository { Account = "second" };
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);
        await library.GetLibraryData();
        await auth.Logout();
        repository.Account = "third";

        var games = await library.GetLibraryData();

        Assert.Equal(["third"], games.Select(game => game.AppName));
        Assert.Equal(2, repository.AssetRequests);
    }

    [Fact]
    public async Task StoreProductDetailsAreCachedUntilInvalidated()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository();
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);

        var first = await library.GetStoreProductDetails("example-namespace");
        var second = await library.GetStoreProductDetails("example-namespace");
        library.InvalidateCache();
        var refreshed = await library.GetStoreProductDetails("example-namespace");

        Assert.Same(first, second);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, repository.ProductRequests);
    }

    [Fact]
    public async Task InFlightStoreDetailsDoNotRepopulateInvalidatedCache()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository { DelayNextProductRequest = true };
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);

        var staleRequest = library.GetStoreProductDetails("example-namespace");
        await repository.ProductRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        library.InvalidateCache();
        repository.DelayedProduct.SetResult(new StoreProductDetails { ProductName = "Stale" });
        await staleRequest.WaitAsync(TimeSpan.FromSeconds(5));

        var refreshed = await library.GetStoreProductDetails("example-namespace");

        Assert.Equal("Product 2", refreshed?.ProductName);
        Assert.Equal(2, repository.ProductRequests);
    }

    [Fact]
    public async Task OlderConcurrentStoreResponseCannotOverwriteNewerResponse()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository { DelayTwoProductRequests = true };
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);

        var olderRequest = library.GetStoreProductDetails("example-namespace");
        var newerRequest = library.GetStoreProductDetails("example-namespace");
        await repository.TwoProductRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        repository.SecondDelayedProduct.SetResult(new StoreProductDetails { ProductName = "Newer" });
        var newer = await newerRequest.WaitAsync(TimeSpan.FromSeconds(5));
        repository.DelayedProduct.SetResult(new StoreProductDetails { ProductName = "Older" });
        await olderRequest.WaitAsync(TimeSpan.FromSeconds(5));

        var cached = await library.GetStoreProductDetails("example-namespace");

        Assert.Same(newer, cached);
        Assert.Equal(2, repository.ProductRequests);
    }

    [Fact]
    public async Task UnavailableStoreDetailsAreRetried()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository { ProductDetailsUnavailable = true };
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);

        await library.GetStoreProductDetails("example-namespace");
        await library.GetStoreProductDetails("example-namespace");

        Assert.Equal(2, repository.ProductRequests);
    }

    [Theory]
    [InlineData(6540L, 1)]
    [InlineData(0L, 1)]
    [InlineData(null, 2)]
    public async Task PlaytimeCachesOnlySuccessfulResults(long? seconds, int expectedRequests)
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.PlaytimeSeconds = seconds;

        Assert.Equal(seconds, await library.GetGamePlaytimeSeconds("second"));
        Assert.Equal(seconds, await library.GetGamePlaytimeSeconds("second"));
        Assert.Equal(expectedRequests, repository.PlaytimeRequests);
    }

    [Fact]
    public async Task PlaytimeInvalidationRefreshesCachedResult()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        Assert.Equal(6540L, await library.GetGamePlaytimeSeconds("second"));
        repository.PlaytimeSeconds = 7200;
        library.InvalidateCache();

        Assert.Equal(7200L, await library.GetGamePlaytimeSeconds("second"));
        Assert.Equal(2, repository.PlaytimeRequests);
    }

    [Fact]
    public async Task LogoutDiscardsInflightPlaytimeAndSeparatesNextAccount()
    {
        var (library, repository, auth) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        var pending = library.GetGamePlaytimeSeconds("second");
        Assert.Equal(1, repository.PlaytimeRequests);

        await auth.Logout();
        repository.DelayedPlaytime.SetResult(99999);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        repository.Account = "third";
        repository.DelayPlaytime = false;
        repository.PlaytimeSeconds = 42;
        await library.GetLibraryData();
        Assert.Null(await library.GetGamePlaytimeSeconds("second"));
        Assert.Equal(42L, await library.GetGamePlaytimeSeconds("third"));
    }

    [Fact]
    public async Task InvalidationDiscardsInflightPlaytime()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        var pending = library.GetGamePlaytimeSeconds("second");
        library.InvalidateCache();
        repository.DelayedPlaytime.SetResult(99999);
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        repository.DelayPlaytime = false;
        repository.PlaytimeSeconds = 42;
        Assert.Equal(42L, await library.GetGamePlaytimeSeconds("second"));
    }

    [Fact]
    public async Task ConcurrentPlaytimeRequestsShareTheCachedResult()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        var first = library.GetGamePlaytimeSeconds("second");
        var second = library.GetGamePlaytimeSeconds("second");
        repository.DelayedPlaytime.SetResult(6540);

        Assert.Equal(new long?[] { 6540, 6540 }, await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, repository.PlaytimeRequests);
    }

    [Fact]
    public async Task CancelledPlaytimeReleasesGateForRetry()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        using var cancellation = new CancellationTokenSource();
        var pending = library.GetGamePlaytimeSeconds("second", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        repository.DelayPlaytime = false;
        Assert.Equal(6540L, await library.GetGamePlaytimeSeconds("second"));
        Assert.Equal(2, repository.PlaytimeRequests);
    }

    [Fact]
    public async Task UnknownGameDoesNotRequestPlaytime()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        Assert.Null(await library.GetGamePlaytimeSeconds("not-owned"));
        Assert.Equal(0, repository.PlaytimeRequests);
    }

    [Fact]
    public async Task GameInfoDisplaysPlaytimeAndIgnoresQueuedUpdatesFromPreviousNavigation()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        var dispatcher = new QueuedDispatcher();
        var viewModel = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, installer, library, storage, dispatcher);

        await viewModel.OnNavigatedTo("second");
        viewModel.OnNavigatedFrom();
        library.InvalidateCache();
        repository.PlaytimeSeconds = 7200;
        await viewModel.OnNavigatedTo("second");
        Assert.Equal("Loading…", viewModel.TotalPlayTimeText);
        dispatcher.Flush();

        Assert.Equal("2h 0m", viewModel.TotalPlayTimeText);
        viewModel.OnNavigatedFrom();
    }

    [Theory]
    [InlineData(6540L, "1h 49m")]
    [InlineData(null, "Unavailable")]
    public async Task GameInfoShowsLoadingUntilPlaytimeRequestFinishes(long? seconds, string expected)
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        var dispatcher = new QueuedDispatcher();
        var viewModel = PlaytimeViewModel(library, repository, dispatcher);
        var pending = viewModel.OnNavigatedTo("second");
        dispatcher.Flush();
        Assert.Equal("Loading…", viewModel.TotalPlayTimeText);

        repository.DelayedPlaytime.SetResult(seconds);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.Flush();
        Assert.Equal(expected, viewModel.TotalPlayTimeText);
        viewModel.OnNavigatedFrom();
    }

    [Fact]
    public async Task FailedRemoteRequestsFinishLoadingAndDisplayUnavailable()
    {
        var (library, repository, _) = await PlaytimeLibrary();
        repository.DelayPlaytime = true;
        repository.DelayNextProductRequest = true;
        var dispatcher = new QueuedDispatcher();
        var viewModel = PlaytimeViewModel(library, repository, dispatcher);
        var pending = viewModel.OnNavigatedTo("second");
        dispatcher.Flush();
        Assert.Equal("Loading…", viewModel.TotalPlayTimeText);
        Assert.Equal("Loading…", viewModel.PublisherName);
        Assert.Equal("Loading…", viewModel.ReleaseDateText);
        Assert.True(viewModel.IsDetailsLoading);

        repository.DelayedPlaytime.SetException(new IOException("Synthetic playtime failure"));
        repository.DelayedProduct.SetException(new IOException("Synthetic Store failure"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.Flush();

        Assert.Equal("Unavailable", viewModel.TotalPlayTimeText);
        Assert.Equal("Unavailable", viewModel.PublisherName);
        Assert.Equal("Unavailable", viewModel.ReleaseDateText);
        Assert.False(viewModel.IsDetailsLoading);
        viewModel.OnNavigatedFrom();
    }

    private GameInfoViewModel PlaytimeViewModel(LibraryManager library, ControlledRepository repository, IUiDispatcher dispatcher)
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        return new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance, installer, library, storage, dispatcher);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _callbacks = new();
        public bool TryEnqueue(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        public void Flush()
        {
            while (_callbacks.TryDequeue(out var callback)) callback();
        }
    }

    private async Task<(LibraryManager Library, ControlledRepository Repository, AuthManager Auth)> PlaytimeLibrary()
    {
        var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
        var repository = new ControlledRepository { Account = "second" };
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
        var library = new LibraryManager(_logger, repository, storage, auth);
        await library.GetLibraryData();
        return (library, repository, auth);
    }

    public void Dispose()
    {
        _http.Dispose();
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ControlledRepository : IStoreRepository
    {
        public string Account = "first";
        public int AssetRequests;
        public int ProductRequests;
        public int PlaytimeRequests;
        public long? PlaytimeSeconds = 6540;
        public bool DelayPlaytime;
        public TaskCompletionSource<long?> DelayedPlaytime { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayNextProductRequest;
        public bool DelayTwoProductRequests;
        public bool ProductDetailsUnavailable;
        public TaskCompletionSource<bool> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Metadata> FirstMetadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ProductRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TwoProductRequestsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<StoreProductDetails?> DelayedProduct { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<StoreProductDetails?> SecondDelayedProduct { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
        {
            AssetRequests++;
            return Task.FromResult<IEnumerable<Asset>>([new Asset {
                AppName = Account, Namespace = "synthetic", CatalogItemId = Account, BuildVersion = "1.0"
            }]);
        }

        public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {
            if (catalogItemId != "first") return Task.FromResult(Metadata(catalogItemId));
            FirstRequest.TrySetResult(true);
            return FirstMetadata.Task;
        }

        public static Metadata Metadata(string app) => new() { Id = app, Namespace = "synthetic", Title = app, KeyImages = [] };

        public Task<long?> FetchGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default)
        {
            PlaytimeRequests++;
            return DelayPlaytime ? DelayedPlaytime.Task.WaitAsync(cancellationToken) : Task.FromResult(PlaytimeSeconds);
        }

        public Task<StoreProductDetails?> FetchStoreProductDetails(
            string nameSpace,
            CancellationToken cancellationToken = default)
        {
            ProductRequests++;
            if (ProductDetailsUnavailable)
                return Task.FromResult<StoreProductDetails?>(null);

            if (DelayTwoProductRequests)
            {
                if (ProductRequests == 2)
                    TwoProductRequestsStarted.TrySetResult(true);
                return ProductRequests == 1 ? DelayedProduct.Task : SecondDelayedProduct.Task;
            }

            if (DelayNextProductRequest)
            {
                DelayNextProductRequest = false;
                ProductRequestStarted.TrySetResult(true);
                return DelayedProduct.Task;
            }

            return Task.FromResult<StoreProductDetails?>(new StoreProductDetails
            {
                ProductName = $"Product {ProductRequests}"
            });
        }
        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
        public Task<string> GetGameToken() => throw new NotSupportedException();
        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
