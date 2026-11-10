using System;
using System.Collections.Generic;
using Crimson.Core;

namespace Crimson.Providers.Ubisoft;

/// <summary>Only public Ubisoft asset hosts may serve artwork the shell downloads.</summary>
public sealed class UbisoftContentPolicy : IProviderContentPolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "static3.cdn.ubi.com",
        "ubistatic3-a.akamaihd.net"
    };

    public string ProviderId => UbisoftLauncherProvider.ProviderId;

    public Uri RequireArtworkUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && Hosts.Contains(uri.Host))
            return uri;
        throw new InvalidOperationException("Ubisoft artwork must come from a Ubisoft content host.");
    }
}
