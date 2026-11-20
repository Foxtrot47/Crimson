using System;

namespace Crimson.Providers;

/// <summary>Providers explicitly validate the destinations used to fetch their game artwork.</summary>
public interface IProviderContentPolicy
{
    string ProviderId { get; }
    Uri RequireArtworkUri(string url);

    /// <summary>Non-throwing form of <see cref="RequireArtworkUri"/> for shared artwork pipelines.</summary>
    bool AllowsArtworkUri(string url)
    {
        try { RequireArtworkUri(url); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
