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
    public async Task FreshCacheAllowsRepeatedAssetsForTheSameAppName()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        storage.SaveMetaData(Game("game"));
        storage.SaveLibraryCache([Asset("game"), Asset("game")], DateTimeOffset.UtcNow);
        var repository = new Repository { FailAssets = true };
        var library = CreateLibrary(CreateStorage(), repository);

        Assert.Equal("game", Assert.Single(await library.GetLibraryData()).AppName);
        Assert.Equal(0, repository.AssetRequests);
    }

    [Fact]
    public async Task CachedLibraryShowsWhileAuthenticationVerificationRuns()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData
        {
            AccountId = "account",
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O")
        });
        storage.SaveMetaData(Game("game"));
        storage.SaveLibraryCache([Asset("game")], DateTimeOffset.UtcNow.AddHours(-1));

        var authHandler = new BlockingAuthHandler();
        using var authHttp = new HttpClient(authHandler);
        var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), authHttp);
        var repository = new Repository();
        var library = new LibraryManager(_logger, repository, storage, auth);
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        var viewModel = new LibraryViewModel(_logger, installer, library, new ImmediateDispatcher());

        var authCheck = auth.CheckAuthStatus();
        await authHandler.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.OnNavigatedTo(null!);

        Assert.False(viewModel.ShowLoadingScreen);
        Assert.Equal("game", Assert.Single(viewModel.GamesList).Name);
        Assert.Equal(0, repository.AssetRequests);

        authHandler.ReleaseRequest.SetResult();
        Assert.Equal(AuthenticationStatus.LoggedIn, await authCheck);
        Assert.Single(await library.GetLibraryData());
        Assert.Equal(1, repository.AssetRequests);
        viewModel.OnNavigatedFrom();
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
    public async Task InvalidSnapshotAssetRecordsAreRefetched()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccountId = "account", UpdatedAt = DateTimeOffset.UtcNow,
            Assets = new object?[] { null }
        });
        File.WriteAllText(Path.Combine(_root, "library-cache.json"), snapshot);
        var repository = new Repository();
        Assert.Single(await CreateLibrary(storage, repository).GetLibraryData());
        Assert.Equal(1, repository.AssetRequests);
    }

    [Fact]
    public async Task InvalidationMarksSnapshotStaleWithoutRewritingIt()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        var repository = new Repository();
        await CreateLibrary(storage, repository).GetLibraryData();
        var cacheFile = Path.Combine(_root, "library-cache.json");
        var written = File.ReadAllText(cacheFile);

        CreateLibrary(storage, repository).InvalidateCache();
        Assert.Equal(written, File.ReadAllText(cacheFile));
        Assert.Equal(DateTimeOffset.MinValue, storage.ReadLibraryCache()!.UpdatedAt);

        Assert.Single(await CreateLibrary(storage, repository).GetLibraryData());
        Assert.Equal(2, repository.AssetRequests);
        Assert.NotEqual(DateTimeOffset.MinValue, storage.ReadLibraryCache()!.UpdatedAt);
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
        Assert.Equal(2, (await library.GetLibraryData()).Count());
        Assert.Empty(await library.GetLibraryData());
        Assert.Empty(await library.GetLibraryData());
        Assert.Equal(4, repository.AssetRequests);
    }

    [Fact]
    public async Task SingleEmptyOwnershipResponseKeepsLibraryAndCache()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        var owned = (await library.GetLibraryData()).Select(game => game.AppName).ToList();
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);

        var assets = repository.Assets.ToList();
        repository.Assets.Clear();
        library.InvalidateCache();
        Assert.Equal(owned, (await library.GetLibraryData()).Select(game => game.AppName));
        Assert.Equal(owned.Count, storage.ReadLibraryCache()!.Assets.Count);

        repository.Assets.AddRange(assets);
        Assert.Equal(owned, (await library.GetLibraryData()).Select(game => game.AppName));
        Assert.Empty(additions);
    }

    [Fact]
    public async Task NewGameIsReportedOnceAfterLibraryUpdateButInitialLoadIsSilent()
    {
        var repository = new Repository();
        var library = CreateLibrary(CreateStorage(), repository);
        var additions = new List<IReadOnlyList<Game>>();
        var ordering = new List<string>();
        library.LibraryUpdated += _ => ordering.Add("library");
        library.GamesAdded += games => { additions.Add(games); ordering.Add("notification"); };
        await library.GetLibraryData();
        Assert.Empty(additions);

        ordering.Clear();
        repository.Assets.Add(Asset("new"));
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Equal("new", Assert.Single(Assert.Single(additions)).AppName);
        Assert.Equal(["library", "notification"], ordering);
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Single(additions);
    }

    [Fact]
    public async Task CachedStartupOnlyReportsGamesAddedSinceSnapshot()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        await CreateLibrary(storage, new Repository()).GetLibraryData();
        var repository = new Repository { Assets = [Asset("game"), Asset("new")] };
        var library = CreateLibrary(CreateStorage(), repository);
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Equal("new", Assert.Single(additions).AppName);
    }

    [Fact]
    public async Task BatchAdditionsExcludeDlcAndMetadataOnlyRefreshes()
    {
        var repository = new Repository();
        var library = CreateLibrary(CreateStorage(), repository);
        var additions = new List<IReadOnlyList<Game>>();
        library.GamesAdded += additions.Add;
        await library.GetLibraryData();
        repository.Assets.AddRange([Asset("second"), Asset("third"), Asset("dlc")]);
        repository.DlcApps.Add("dlc");
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Equal(["second", "third"], Assert.Single(additions).Select(game => game.AppName));
        await library.GetLibraryData(forceUpdate: true);
        Assert.Single(additions);
    }

    [Fact]
    public async Task DelayedMetadataNotifiesOnlyWhenNewGameCanBeDisplayed()
    {
        var repository = new Repository();
        var library = CreateLibrary(CreateStorage(), repository);
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        await library.GetLibraryData();
        repository.Assets.Add(Asset("new"));
        repository.MissingMetadataApps.Add("new");
        library.InvalidateCache();
        Assert.Single(await library.GetLibraryData());
        Assert.Empty(additions);
        repository.MissingMetadataApps.Clear();
        Assert.Equal(2, (await library.GetLibraryData()).Count());
        Assert.Equal("new", Assert.Single(additions).AppName);
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Single(additions);
    }

    [Fact]
    public async Task InitialMetadataRetryAndAccountSwitchAreSilent()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "first" });
        var repository = new Repository { FailMetadata = true };
        var library = CreateLibrary(storage, repository);
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        await library.GetLibraryData();
        repository.FailMetadata = false;
        await library.GetLibraryData();
        Assert.Empty(additions);
        await storage.SaveUserData(new UserData { AccountId = "second" });
        repository.Assets = [Asset("other")];
        await library.GetLibraryData();
        Assert.Empty(additions);
    }

    [Fact]
    public async Task PendingAdditionCannotCrossAccounts()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "first" });
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        await library.GetLibraryData();
        repository.Assets.Add(Asset("new"));
        repository.MissingMetadataApps.Add("new");
        library.InvalidateCache();
        await library.GetLibraryData();
        await storage.SaveUserData(new UserData { AccountId = "second" });
        repository.MissingMetadataApps.Clear();
        await library.GetLibraryData();
        Assert.Empty(additions);
    }

    [Fact]
    public async Task AccountChangeInsideLibraryHandlerSuppressesObsoleteAddition()
    {
        var storage = CreateStorage();
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        await library.GetLibraryData();
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        library.LibraryUpdated += _ => storage.ClearUserData().GetAwaiter().GetResult();
        repository.Assets.Add(Asset("new"));
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Empty(additions);
    }

    [Fact]
    public async Task NewGameAfterEmptyBaselineIsReportedButFailedRefreshIsSilent()
    {
        var repository = new Repository { Assets = [] };
        var library = CreateLibrary(CreateStorage(), repository);
        var additions = new List<Game>();
        library.GamesAdded += games => additions.AddRange(games);
        await library.GetLibraryData();
        repository.Assets.Add(Asset("new"));
        repository.FailAssets = true;
        library.InvalidateCache();
        await library.GetLibraryData();
        Assert.Empty(additions);
        repository.FailAssets = false;
        await library.GetLibraryData();
        Assert.Equal("new", Assert.Single(additions).AppName);
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
    public async Task CachedGamesArePublishedBeforeOwnershipRevalidationCompletes()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        await CreateLibrary(storage, new Repository()).GetLibraryData();

        var reloadedStorage = CreateStorage();
        var enteredRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository { BeforeAssets = async () => { enteredRefresh.SetResult(); await releaseRefresh.Task; } };
        var library = CreateLibrary(reloadedStorage, repository);
        library.InvalidateCache();
        var installer = new InstallManager(_logger, library, repository, reloadedStorage, null!, null!, null!);
        var viewModel = new LibraryViewModel(_logger, installer, library, new ImmediateDispatcher());

        var navigation = viewModel.OnNavigatedTo(null!);
        await enteredRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(navigation.IsCompleted);
        Assert.False(viewModel.ShowLoadingScreen);
        Assert.Equal("game", Assert.Single(viewModel.GamesList).Name);

        releaseRefresh.SetResult();
        await navigation;
        viewModel.OnNavigatedFrom();
    }

    [Fact]
    public async Task ExistingAssetsFileSeedsLibraryBeforeFirstVersionedCacheRefresh()
    {
        var storage = CreateStorage();
        await storage.SaveUserData(new UserData { AccountId = "account" });
        storage.SaveGameAssetsData([Asset("game")]);
        storage.SaveMetaData(Game("game"));

        var enteredRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository { BeforeAssets = async () => { enteredRefresh.SetResult(); await releaseRefresh.Task; } };
        var restoredStorage = CreateStorage();
        var library = CreateLibrary(restoredStorage, repository);
        var installer = new InstallManager(_logger, library, repository, restoredStorage, null!, null!, null!);
        var viewModel = new LibraryViewModel(_logger, installer, library, new ImmediateDispatcher());

        var navigation = viewModel.OnNavigatedTo(null!);
        await enteredRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(navigation.IsCompleted);
        Assert.False(viewModel.ShowLoadingScreen);
        Assert.Equal("game", Assert.Single(viewModel.GamesList).Name);

        releaseRefresh.SetResult();
        await navigation;
        viewModel.OnNavigatedFrom();
    }

    [Fact]
    public async Task NavigationKeepsUnchangedCardsAndDoesNotRefetchLibrary()
    {
        var repository = new Repository();
        var storage = CreateStorage();
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        var vm = new LibraryViewModel(_logger, installer, library, new ImmediateDispatcher());
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
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        var vm = new LibraryViewModel(_logger, installer, library, dispatcher);
        await vm.OnNavigatedTo(null!);
        await storage.ClearUserData();
        dispatcher.Flush();
        Assert.Empty(vm.GamesList);
        vm.OnNavigatedFrom();
    }

    [Theory]
    [InlineData("https://store.epicgames.com/en-US/", false)]
    [InlineData("https://store.epicgames.com/en-US/p/game?checkout=true", false)]
    [InlineData("https://store.epicgames.com/en-US/p/checkout", false)]
    [InlineData("https://store.epicgames.com/en-US/p/purchase", false)]
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
    public async Task TransientArtworkFailureIsRetriedWithBackoffWithinOneRequest()
    {
        var handler = new ImageHandler { FailuresBeforeSuccess = 2, Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(3, handler.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork"), "*.tmp"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ClientErrorsAreNotRetried(HttpStatusCode status)
    {
        var handler = new ImageHandler { Status = status };
        using var http = new HttpClient(handler);
        Assert.Null(await CreateArtworkCache(http).GetImageUriAsync(ImageUrl));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task FailedArtworkIsNotRequestedAgainUntilCooldownEnds()
    {
        var handler = new ImageHandler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler);
        var clock = new SteppingTimeProvider();
        var cache = CreateArtworkCache(http, clock);
        Assert.Null(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(3, handler.Requests);

        handler.Status = HttpStatusCode.OK;
        Assert.Null(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(3, handler.Requests);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task ClearingArtworkResetsFailureCooldown()
    {
        var handler = new ImageHandler { Status = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var cache = CreateArtworkCache(http);
        Assert.Null(await cache.GetImageUriAsync(ImageUrl));
        handler.Status = HttpStatusCode.OK;
        Assert.True(cache.Clear());
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task LibraryRetriesMissingArtworkOnLibraryUpdateButNotOnOperationProgress()
    {
        var storage = CreateStorage();
        var repository = new Repository();
        var library = CreateLibrary(storage, repository);
        var installer = new InstallManager(_logger, library, repository, storage, null!, null!, null!);
        var artwork = new CountingArtworkCache();
        var vm = new LibraryViewModel(_logger, installer, library, new ImmediateDispatcher(), artwork);
        await vm.OnNavigatedTo(null!);
        Assert.True(artwork.Requests > 0);
        Assert.Null(Assert.Single(vm.GamesList).ImageUrl);
        var requestsAfterLoad = artwork.Requests;

        var refreshOperationStatus = typeof(LibraryViewModel).GetMethod("RefreshOperationStatus",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var generation = (int)typeof(LibraryViewModel).GetField("_navigationGeneration",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        refreshOperationStatus.Invoke(vm, [generation, false]);
        Assert.Equal(requestsAfterLoad, artwork.Requests);

        artwork.Result = "file:///artwork.png";
        library.InvalidateCache();
        await library.GetLibraryData();
        await WaitForAsync(() => artwork.Requests > requestsAfterLoad);
        Assert.Equal("file:///artwork.png", Assert.Single(vm.GamesList).ImageUrl);
        vm.OnNavigatedFrom();
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
        var clock = new SteppingTimeProvider();
        var cache = CreateArtworkCache(http, clock);
        Assert.Null(await cache.GetImageUriAsync(ImageUrl));
        handler.Status = HttpStatusCode.OK;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(4, handler.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork"), "*.tmp"));
    }

    [Theory]
    [InlineData("text/html", false)]
    [InlineData("image/png", true)]
    [InlineData("image/png", false)]
    public async Task InvalidArtworkResponsesDoNotPoisonCache(string type, bool empty)
    {
        var handler = new ImageHandler { ContentType = type, Empty = empty, InvalidPayload = !empty && type == "image/png" };
        using var http = new HttpClient(handler);
        Assert.Null(await CreateArtworkCache(http).GetImageUriAsync(ImageUrl));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork")));
    }

    [Fact]
    public async Task StalledArtworkBodyTimesOutAndReleasesDownloadSlot()
    {
        var handler = new ImageHandler { StalledBody = true };
        using var http = new HttpClient(handler);
        var cache = new ArtworkCache(http, _logger, Path.Combine(_root, "artwork"), TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

        var paths = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(index => cache.GetImageUriAsync($"https://cdn1.epicgames.com/test/{index}.png")));

        Assert.All(paths, Assert.Null);
        var requestsBeforeRetry = handler.Requests;
        handler.StalledBody = false;
        Assert.NotNull(await cache.GetImageUriAsync("https://cdn1.epicgames.com/test/retry.png"));
        Assert.Equal(requestsBeforeRetry + 1, handler.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "artwork"), "*.tmp"));
    }

    [Fact]
    public async Task InvalidArtworkRefreshPreservesExistingCachedImage()
    {
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        var clock = new SteppingTimeProvider();
        var cache = CreateArtworkCache(http, clock);
        var uri = await cache.GetImageUriAsync(ImageUrl);
        var path = new Uri(uri!).LocalPath;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
        handler.InvalidPayload = true;

        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(ImageHandler.Png, File.ReadAllBytes(path));

        handler.InvalidPayload = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        Assert.Equal(ImageHandler.Png, File.ReadAllBytes(path));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ExpiredArtworkUsesOldFileOfflineAndRefreshesWhenAvailable()
    {
        var handler = new ImageHandler();
        using var http = new HttpClient(handler);
        var clock = new SteppingTimeProvider();
        var cache = CreateArtworkCache(http, clock);
        var uri = await cache.GetImageUriAsync(ImageUrl);
        var path = new Uri(uri!).LocalPath;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        handler.Status = HttpStatusCode.OK;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(uri, await cache.GetImageUriAsync(ImageUrl));
        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(5, handler.Requests);
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
        const string firstImage = "https://cdn1.epicgames.com/first.png";
        const string secondImage = "https://cdn1.epicgames.com/second.png";
        artwork.Complete(secondImage);
        await second;
        await WaitForAsync(() => vm.CoverImageUrl == secondImage);
        artwork.Complete(firstImage);
        await first;
        await Task.Delay(50);
        Assert.Equal("second", vm.Game?.AppName);
        Assert.Equal(secondImage, vm.CoverImageUrl);
        vm.OnNavigatedFrom();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private ArtworkCache CreateArtworkCache(HttpClient http, TimeProvider? clock = null) =>
        new(http, _logger, Path.Combine(_root, "artwork"), TimeSpan.FromSeconds(30), TimeSpan.Zero, clock);
    private const string ImageUrl = "https://cdn1.epicgames.com/test/image.png";
    private static Asset Asset(string name) => new() { AppName = name, CatalogItemId = name, Namespace = "synthetic", BuildVersion = "1" };
    private static Game Game(string name) => new()
    {
        AppName = name, AppTitle = name, AssetInfos = new AssetInfos { Windows = Asset(name) },
        Metadata = new Metadata { Id = name, Title = name, KeyImages = [new KeyImage { Type = "DieselGameBox", Url = $"https://cdn1.epicgames.com/{name}.png" }] }
    };

    private sealed class Repository : IStoreRepository
    {
        public List<Asset> Assets = [Asset("game")];
        public bool FailAssets, FailMetadata;
        public HashSet<string> MissingMetadataApps = [];
        public HashSet<string> DlcApps = [];
        public int AssetRequests, MetadataRequests;
        public Func<Task>? BeforeMetadata;
        public Func<Task>? BeforeAssets;
        public async Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
        {
            AssetRequests++;
            if (BeforeAssets is not null) await BeforeAssets();
            return FailAssets ? throw new HttpRequestException("offline") : Assets.ToList();
        }
        public async Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
        {
            Interlocked.Increment(ref MetadataRequests);
            if (BeforeMetadata is not null) await BeforeMetadata();
            if (FailMetadata || MissingMetadataApps.Contains(catalogItemId)) return null!;
            var metadata = Game(catalogItemId).Metadata;
            if (DlcApps.Contains(catalogItemId)) metadata.MainGameItem = new MainGameItem { Id = "base" };
            return metadata;
        }
        public Task<StoreProductDetails?> FetchStoreProductDetails(string nameSpace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> FetchGamePlaytimeSeconds(string appName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
        public Task<string> GetGameToken() => throw new NotSupportedException();
        public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
        public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingAuthHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestEntered.SetResult();
            await ReleaseRequest.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        public int Requests;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string ContentType = "image/png";
        public bool Empty;
        public bool InvalidPayload;
        public bool StalledBody;
        public int FailuresBeforeSuccess = -1;
        public Func<Task>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Requests);
            if (BeforeResponse is not null) await BeforeResponse();
            HttpContent content = StalledBody
                ? new StalledContent()
                : new ByteArrayContent(Empty ? [] : InvalidPayload ? [0x01] : Png);
            var status = FailuresBeforeSuccess >= 0 && count > FailuresBeforeSuccess ? HttpStatusCode.OK : Status;
            var response = new HttpResponseMessage(status) { Content = content };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            return response;
        }
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.InfiniteTimeSpan);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new StalledStream());
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ControlledArtworkCache : IArtworkCache
    {
        private readonly Dictionary<string, TaskCompletionSource<string?>> _requests = new();
        public Task<string?> GetImageUriAsync(string? url)
        {
            if (!_requests.TryGetValue(url!, out var source))
            {
                source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _requests.Add(url!, source);
            }
            return source.Task;
        }
        public void Complete(string url) => _requests[url].SetResult(url);
        public int Generation => 0;
        public long GetSizeBytes() => 0;
        public bool Clear() => true;
    }

    private sealed class CountingArtworkCache : IArtworkCache
    {
        public int Requests;
        public string? Result;
        public Task<string?> GetImageUriAsync(string? url)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Result);
        }
        public int Generation => 0;
        public long GetSizeBytes() => 0;
        public bool Clear() => true;
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
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
