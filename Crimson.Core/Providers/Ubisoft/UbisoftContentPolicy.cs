using System;
using Crimson.Core;

namespace Crimson.Providers.Ubisoft;

/// <summary>
/// Only Ubisoft's public asset host may serve artwork the shell downloads: every image URL comes from a product
/// configuration through <see cref="UbisoftProductConfig.AssetUrl"/>.
/// </summary>
public sealed class UbisoftContentPolicy : IProviderContentPolicy
{
    private const string AssetHost = "static3.cdn.ubi.com";

    public string ProviderId => UbisoftLauncherProvider.ProviderId;

    public Uri RequireArtworkUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, AssetHost, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0)
            return uri;
        throw new InvalidOperationException("Ubisoft artwork must come from a Ubisoft content host.");
    }
}
