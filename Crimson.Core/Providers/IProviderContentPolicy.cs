using System;

namespace Crimson.Providers;

/// <summary>Providers explicitly validate the destinations used to fetch their game artwork.</summary>
public interface IProviderContentPolicy
{
    string ProviderId { get; }
    Uri RequireArtworkUri(string url);
}
