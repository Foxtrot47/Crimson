using System.Net;
using System.Net.Http.Headers;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class CachingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-cache-{Guid.NewGuid():N}");
    private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
    private readonly HttpClient _authHttp = new();

    private Storage CreateStorage() => new(_logger, _root, Path.Combine(_root, "games"));
    private LibraryManager CreateLibrary(Storage storage, Repository repository) => new(
        _logger, repository, storage, new AuthManager(_logger, storage, new TestCredentialProtector(), _authHttp));

    [Fact]
    public async Task FreshLibrarySurvivesRestartWithoutNetworkAndHydratesInstallState()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        var repository = new Repository();
        await CreateLibrary(storage, repository).GetLibraryData();
        storage.AddToLocalAppState("game", new LocalAppState { InstallStatus = InstallState.Installed, Version = "1" });
        storage.SaveMetaData(Game("unowned"));
        var reloaded = CreateStorage();
        var library = CreateLibrary(reloaded, repository);
        var published = new List<Game>();
        library.LibraryUpdated += games => published.AddRange(games);

        var game = Assert.Single(await library.GetLibraryData());

        Assert.Equal("game", game.AppName);
        Assert.Equal(InstallState.Installed, game.LocalAppState?.InstallStatus);
        Assert.Single(published);
        Assert.Equal(1, repository.AssetRequests);
        Assert.Equal(1, repository.MetadataRequests);
    }

    [Fact]
    public async Task ExpiredLibraryFallsBackToDiskOnNetworkFailureAndRetries()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        storage.SaveMetaData(Game("game"));
        storage.SaveLibraryCache([Asset("game")], DateTimeOffset.UtcNow.AddHours(-1));
        var repository = new Repository { FailAssets = true };
        var library = CreateLibrary(CreateStorage(), repository);

        Assert.Single(await library.GetLibraryData());
        repository.FailAssets = false;
        Assert.Single(await library.GetLibraryData());
        Assert.Equal(2, repository.AssetRequests);
        Assert.Equal(0, repository.MetadataRequests);
    }

    [Fact]
    public async Task CorruptSnapshotIsRefetched()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        File.WriteAllText(Path.Combine(_root, "library-cache.json"), "{bad json");
        var repository = new Repository();
        Assert.Single(await CreateLibrary(storage, repository).GetLibraryData());
        Assert.Equal(1, repository.AssetRequests);
    }

    [Fact]
    public async Task SnapshotBelongsToAccountAndLogoutDeletesIt()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "first" });
        await CreateLibrary(storage, new Repository()).GetLibraryData();
        var snapshot = File.ReadAllText(Path.Combine(_root, "library-cache.json"));
        await storage.SaveUserData(new UserData { AccountId = "second" });
        // Even if an old snapshot is restored independently, it cannot confer ownership.
        File.WriteAllText(Path.Combine(_root, "library-cache.json"), snapshot);
        var repository = new Repository { FailAssets = true };
        Assert.Empty(await CreateLibrary(storage, repository).GetLibraryData());
        Assert.Empty(storage.GameMetaDataDictionary);
        await storage.ClearUserData();
        Assert.False(File.Exists(Path.Combine(_root, "library-cache.json")));
    }

    [Fact]
    public async Task InvalidationSurvivesRestart()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        var library = CreateLibrary(storage, new Repository());
        await library.GetLibraryData();
        library.InvalidateCache();
        var repository = new Repository { Assets = [Asset("new")] };
        Assert.Equal("new", Assert.Single(await CreateLibrary(CreateStorage(), repository).GetLibraryData()).AppName);
        Assert.Equal(1, repository.AssetRequests);
    }

    [Fact]
    public void NewCheckoutDuringRefreshRemainsDirty()
    {
        var policy = new StoreLibraryRefreshPolicy();
        policy.ObserveNavigation("https://store.epicgames.com/purchase");
        var revision = policy.Revision;
        policy.ObserveNavigation("https://store.epicgames.com/purchase");
        policy.MarkOwnershipRefreshed(revision);
        Assert.True(policy.OwnershipMayHaveChanged);
    }

    [Fact]
    public async Task MissingMetadataIsRetriedRatherThanCachedAsComplete()
    {
        var repository = new Repository { FailMetadata = true };
        var library = CreateLibrary(CreateStorage(), repository);
        Assert.Empty(await library.GetLibraryData());
        repository.FailMetadata = false;
        Assert.Single(await library.GetLibraryData());
        Assert.Equal(2, repository.MetadataRequests);
    }

    [Fact]
    public async Task OwnershipRefreshFetchesOnlyNewMetadataAndCanRemoveAllGames()
    {
        var repository = new Repository();
        var library = CreateLibrary(CreateStorage(), repository);
        await library.GetLibraryData();
        repository.Assets.Add(Asset("new"));
        library.InvalidateCache();
        Assert.Equal(2, (await library.GetLibraryData()).Count());
        Assert.Equal(2, repository.MetadataRequests);
        repository.Assets.Clear();
        library.InvalidateCache();
        Assert.Empty(await library.GetLibraryData());
        Assert.Empty(await library.GetLibraryData());
        Assert.Equal(3, repository.AssetRequests);
    }

    [Fact]
    public async Task InvalidationBeforeDiskHydrationIsNotLost()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        await CreateLibrary(storage, new Repository()).GetLibraryData();
        var repository = new Repository { Assets = [Asset("new")] };
        var library = CreateLibrary(CreateStorage(), repository);
        library.InvalidateCache();
        Assert.Equal("new", Assert.Single(await library.GetLibraryData()).AppName);
        Assert.Equal(1, repository.AssetRequests);
    }

    [Fact]
    public async Task InvalidationDuringRefreshForcesAnotherOwnershipCheck()
    {
        var repository = new Repository();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.BeforeMetadata = async () => { entered.SetResult(); await release.Task; };
        var library = CreateLibrary(CreateStorage(), repository);
        var pending = library.GetLibraryData();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        library.InvalidateCache();
        release.SetResult();
        await pending;
        await library.GetLibraryData();
        Assert.Equal(2, repository.AssetRequests);
    }

    [Fact]
    public async Task NavigationKeepsUnchangedCardsAndDoesNotRefetchLibrary()
    {
        var repository = new Repository();
        var library = CreateLibrary(CreateStorage(), repository);
        var vm = new LibraryViewModel(_logger, library, new ImmediateDispatcher());
        await vm.OnNavigatedTo(null!);
        var list = vm.GamesList;
        var card = Assert.Single(list);
        vm.OnNavigatedFrom();
        await vm.OnNavigatedTo(null!);
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Same(list, vm.GamesList);
        Assert.Same(card, Assert.Single(vm.GamesList));
        Assert.Equal(2, repository.AssetRequests);
        Assert.Equal(1, repository.MetadataRequests);
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task QueuedLibraryUpdateCannotRepopulatePreviousAccount()
    {
        var storage = CreateStorage();
        var dispatcher = new QueuedDispatcher();
        var vm = new LibraryViewModel(_logger, CreateLibrary(storage, new Repository()), dispatcher);
        await vm.OnNavigatedTo(null!);
        await storage.ClearUserData();
        dispatcher.Flush();
        Assert.Empty(vm.GamesList);
        vm.OnNavigatedFrom();
    }

    [Theory]
    [InlineData("https://store.epicgames.com/en-US/", false)]
    [InlineData("https://store.epicgames.com/en-US/p/game?checkout=true", false)]
    [InlineData("https://store.epicgames.com/en-US/purchase", true)]
    [InlineData("https://payment-website-pci.ol.epicgames.com/", true)]
    [InlineData("https://store.epicgames.com/checkout/order", true)]
    [InlineData("https://epicgames.com.evil.test/purchase", false)]
    [InlineData("http://store.epicgames.com/purchase", false)]
    public void StoreOnlyInvalidatesAfterCheckout(string uri, bool expected)
    {
        var policy = new StoreLibraryRefreshPolicy();
        policy.ObserveNavigation(uri);
        Assert.Equal(expected, policy.OwnershipMayHaveChanged);
        policy.ObserveNavigation("https://store.epicgames.com/en-US/");
        Assert.Equal(expected, policy.OwnershipMayHaveChanged);
        policy.MarkOwnershipRefreshed(policy.Revision);
        Assert.False(policy.OwnershipMayHaveChanged);
    }

    [Fact]
    public async Task ArtworkIsSharedAcrossConcurrentRequestsAndProcessRestarts()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ImageHandler { BeforeResponse = () => gate.Task };
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        var first = cache.GetImageUriAsync(ImageUrl);
        var second = cache.GetImageUriAsync(ImageUrl);
        gate.SetResult();
        var paths = await Task.WhenAll(first, second);
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(ImageHandler.Png, File.ReadAllBytes(new Uri(paths[0]!).LocalPath));
        Assert.Equal(paths[0], await CreateArtworkCache(http).GetImageUriAsync(ImageUrl));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LockedExpiredArtworkDoesNotFailANewDownload()
    {
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        var directory = Path.Combine(_root, "artwork");
        Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, "expired.image");
        File.WriteAllBytes(expired, ImageHandler.Png);
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-31));

        // On Windows an open handle blocks deletion, as when an image is being displayed.
        using (File.Open(expired, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));

        Assert.Equal(1, handler.Requests);
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task FailedArtworkDownloadIsNotCachedAndIsRetried()
    {
        var handler = new ImageHandler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        Assert.Null(await cache.GetImageUriAsync(ImageUrl));
        handler.Status = HttpStatusCode.OK;
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(2, handler.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork"), "*.tmp"));
    }

    [Theory]
    [InlineData("text/html", false)]
    [InlineData("image/png", true)]
    public async Task InvalidArtworkResponsesDoNotPoisonCache(string type, bool empty)
    {
        var handler = new ImageHandler { ContentType = type, Empty = empty };
        using var http = new HttpClient(handler);
        Assert.Null(await CreateArtworkCache(http).GetImageUriAsync(ImageUrl));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork")));
    }

    [Fact]
    public async Task ExpiredArtworkUsesOldFileOfflineAndRefreshesWhenAvailable()
    {
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        var uri = await cache.GetImageUriAsync(ImageUrl);
        var path = new Uri(uri!).LocalPath;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        handler.Status = HttpStatusCode.OK;
        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ChangedArtworkUrlUsesDifferentCacheEntry()
    {
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        Assert.NotEqual(await cache.GetImageUriAsync(ImageUrl), await cache.GetImageUriAsync(ImageUrl + "?v=2"));
        Assert.Equal(2, handler.Requests);
        Assert.Null(await cache.GetImageUriAsync("file:///etc/passwd"));
        Assert.Null(await cache.GetImageUriAsync("https://untrusted.test/image.png"));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task GameArtworkCompletionCannotOverwriteNewNavigation()
    {
        var storage = CreateStorage();
        storage.SaveMetaData(Game("first"));
        storage.SaveMetaData(Game("second"));
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage,
            new DownloadManager(_logger, _authHttp), new UnusedShortcutManager(), new AllowPermissionChecker());
        var artwork = new ControlledArtworkCache();
        var vm = new GameInfoViewModel(NullLogger<GameInfoViewModel>.Instance,
            installer, library, storage, new ImmediateDispatcher(), artwork);
        var first = vm.OnNavigatedTo("first");
        var second = vm.OnNavigatedTo("second");
        artwork.Complete("second");
        await second;
        artwork.Complete("first");
        await first;
        Assert.Equal("second", vm.Game?.AppName);
        Assert.Equal("second", vm.TitleImageUrl);
        vm.OnNavigatedFrom();
    }

    private ArtworkCache CreateArtworkCache(HttpClient http) => new(http, _logger, Path.Combine(_root, "artwork"));
    private const string ImageUrl = "https://cdn1.epicgames.com/test/image.png";
    private static Asset Asset(string name) => new() { AppName = name, CatalogItemId = name, Namespace = "synthetic", BuildVersion = "1" };
    private static Game Game(string name) => new()
    {
        AppName = name, AppTitle = name, AssetInfos = new AssetInfos { Windows = Asset(name) },
        Metadata = new Metadata { Id = name, Title = name, KeyImages = [new KeyImage { Type = "DieselGameBox", Url = name }] }
    };

    private sealed class Repository : IStoreRepository
    {
        public List<Asset> Assets = [Asset("game")];
        public bool FailAssets, FailMetadata;
        public int AssetRequests, MetadataRequests;
        public Func<Task>? BeforeMetadata;
        public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
        {
            AssetRequests++;
            return FailAssets ? Task.FromException<IEnumerable<Asset>>(new HttpRequestException("offline")) : Task.FromResult<IEnumerable<Asset>>(Assets.ToList());
        }
        public async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {
            Interlocked.Increment(ref MetadataRequests);
            if (BeforeMetadata is not null) await BeforeMetadata();
            return FailMetadata ? null! : Game(catalogItemId).Metadata;
        }
        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
        public Task<string> GetGameToken() => throw new NotSupportedException();
        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        public int Requests;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string ContentType = "image/png";
        public bool Empty;
        public Func<Task>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (BeforeResponse is not null) await BeforeResponse();
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Empty ? [] : Png) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            return response;
        }
    }

    private sealed class ControlledArtworkCache : IArtworkCache
    {
        private readonly Dictionary<string, TaskCompletionSource<string?>> _requests = new();
        public Task<string?> GetImageUriAsync(string? url)
        {
            var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(url!, source);
            return source.Task;
        }
        public void Complete(string url) => _requests[url].SetResult(url);
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();
        public bool TryEnqueue(Action action) { _actions.Enqueue(action); return true; }
        public void Flush() { while (_actions.TryDequeue(out var action)) action(); }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action) { action(); return true; }
    }

    private sealed class UnusedShortcutManager : IGameShortcutManager
    {
        public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(Game game) { }
    }

    private sealed class AllowPermissionChecker : IInstallPermissionChecker
    {
        public InstallPermissionCheckResult Check(string folderPath) => new(true);
    }

    public void Dispose()
    {
        _authHttp.Dispose();
        _logger.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
