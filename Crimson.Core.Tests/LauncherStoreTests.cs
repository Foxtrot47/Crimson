using Crimson.Providers;

namespace Crimson.Tests;

public sealed class LauncherStoreTests
{
    [Fact]
    public async Task SearchCombinesAllStoresAndKeepsSameProductIdsSeparate()
    {
        var ea = new TestLauncherProvider("ea") { Store = new TestStore("ea") };
        var ubisoft = new TestLauncherProvider("ubisoft") { Store = new TestStore("ubisoft") };
        var stores = new LauncherStores(new LauncherRegistry([ea, ubisoft]));
        var products = await stores.SearchStore("game");
        Assert.Equal(2, products.Count);
        Assert.Equal(["ea", "ubisoft"], products.Select(product => product.ProviderId));
        Assert.All(products, product => Assert.Equal("123", product.ProductId));
        Assert.Equal("ea.example.test", stores.ProductDestination(products[0]).Uri.Host);
        Assert.Equal("ubisoft.example.test", stores.ProductDestination(products[1]).Uri.Host);
        Assert.Equal("EA Store", stores.DisplayName("ea"));
    }

    [Fact]
    public async Task StoreFailureDoesNotDiscardOtherStoresResults()
    {
        var ea = new TestLauncherProvider("ea") { Store = new TestStore("ea", fails: true) };
        var ubisoft = new TestLauncherProvider("ubisoft") { Store = new TestStore("ubisoft") };
        var products = await new LauncherStores(new LauncherRegistry([ea, ubisoft])).SearchStore("game");
        Assert.Equal("ubisoft", Assert.Single(products).ProviderId);
    }

    [Fact]
    public async Task SearchCancellationRemainsCancellation()
    {
        var ea = new TestLauncherProvider("ea") { Store = new TestStore("ea") };
        var stores = new LauncherStores(new LauncherRegistry([ea]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.SearchStore("game", cancellation.Token));
    }

    [Fact]
    public async Task ProvidersWithoutStoreSupportDoNotPreventSearch()
    {
        var stores = new LauncherStores(new LauncherRegistry([new TestLauncherProvider("ea")]));
        Assert.False(stores.HasStores);
        Assert.Empty(await stores.SearchStore("game"));
    }

    private sealed class TestStore(string providerId, bool fails = false) : ILauncherStore
    {
        public string DisplayName => providerId.ToUpperInvariant() + " Store";
        public Uri HomeUri => new($"https://{providerId}.example.test/");
        public Uri CreateSearchUri(string query) => new(HomeUri, "search?q=" + Uri.EscapeDataString(query));
        public Uri CreateProductUri(string productId) => new(HomeUri, "product/" + Uri.EscapeDataString(productId));
        public Task<IReadOnlyList<StoreProduct>> SearchStore(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fails) throw new HttpRequestException("Store unavailable");
            return Task.FromResult<IReadOnlyList<StoreProduct>>([new(providerId, "Test Game", "123", null)]);
        }
    }
}
