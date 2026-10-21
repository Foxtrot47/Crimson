using System;
using Crimson.Core;

namespace Crimson.Providers.Epic;

public sealed class EpicContentPolicy : IProviderContentPolicy
{
    public string ProviderId => EpicLauncherProvider.ProviderId;
    public Uri RequireArtworkUri(string url) => EpicEndpointPolicy.RequireContentUri(url);
}
