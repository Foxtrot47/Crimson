using System;

namespace Crimson.Core;

/// <summary>Browsing leaves ownership intact; entering checkout may change it.</summary>
public sealed class StoreLibraryRefreshPolicy
{
    private int _refreshedRevision;
    public int Revision { get; private set; }
    public bool OwnershipMayHaveChanged => Revision != _refreshedRevision;

    public void ObserveNavigation(string rawUri)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.Host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase))
            return;

        // Checkout can be a top-level page, an iframe, or a popup. Invalidate on an
        // attempt too: the browser may close before a completion signal is visible.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("payment-website-pci.ol.epicgames.com", StringComparison.OrdinalIgnoreCase) ||
            Array.Exists(segments, segment => segment.Equals("purchase", StringComparison.OrdinalIgnoreCase) ||
                                              segment.Equals("checkout", StringComparison.OrdinalIgnoreCase)))
            Revision++;
    }

    public void MarkOwnershipRefreshed(int revision) => _refreshedRevision = Math.Max(_refreshedRevision, revision);
}
