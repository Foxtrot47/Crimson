using System.Collections.Generic;
using System.Linq;
using System;

namespace Crimson.Providers;

public sealed class LauncherRegistry
{
    private readonly Dictionary<string, ILauncherProvider> _providers;
    public IReadOnlyList<ILauncherProvider> Providers { get; }

    public LauncherRegistry(IEnumerable<ILauncherProvider> providers)
    {
        var list = providers.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one launcher provider must be registered.", nameof(providers));
        _providers = new(StringComparer.Ordinal);
        foreach (var provider in list)
        {
            GameId.ValidateProviderId(provider.Id);
            if (!_providers.TryAdd(provider.Id, provider))
                throw new ArgumentException($"Duplicate launcher provider: {provider.Id}", nameof(providers));
        }
        Providers = list.AsReadOnly();
    }

    public ILauncherProvider Get(string id) => _providers.TryGetValue(id, out var provider)
        ? provider : throw new KeyNotFoundException($"Launcher provider '{id}' is not registered.");

}
