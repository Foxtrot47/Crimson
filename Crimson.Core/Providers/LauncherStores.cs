using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Serilog;

namespace Crimson.Providers;

/// <summary>Searches every registered store; each result retains its origin for navigation.</summary>
public sealed class LauncherStores(LauncherRegistry registry, ILogger? log = null)
{
    public IReadOnlyList<ILauncherProvider> Providers => registry.Providers.Where(provider => provider.Store is not null).ToList();
    public bool HasStores => Providers.Count > 0;

    public async Task<IReadOnlyList<StoreProduct>> SearchStore(string query, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(Providers.Select(async provider =>
        {
            try
            {
                var products = await provider.Store!.SearchStore(query, cancellationToken);
                if (products.Any(product => product.ProviderId != provider.Id))
                    throw new InvalidOperationException("Store returned a foreign product identity.");
                return products;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log?.Warning(ex, "Could not search store for {ProviderId}", provider.Id);
                return (IReadOnlyList<StoreProduct>)[];
            }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return results.SelectMany(products => products).ToList();
    }

    public string DisplayName(string providerId) => registry.Get(providerId).Store?.DisplayName
        ?? registry.Get(providerId).DisplayName;
    public StoreDestination ProductDestination(StoreProduct product) => new(product.ProviderId,
        registry.Get(product.ProviderId).Store?.CreateProductUri(product.ProductId)
            ?? throw new NotSupportedException("Provider does not supply a store."));
}
