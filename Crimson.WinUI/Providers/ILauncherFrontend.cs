using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.UI.Xaml;

namespace Crimson.Providers;

/// <summary>Space a provider asks for when its sign-in view is hosted in a dialog.</summary>
public sealed record LauncherSignInSize(double Width, double Height);

public interface ILauncherSignInView
{
    FrameworkElement Element { get; }
    void InitializeSignIn();
    void CloseSignIn();

    /// <summary>Browser-based flows need a page; forms need much less. Defaults to the browser size.</summary>
    LauncherSignInSize PreferredSize => new(900, 620);
}

public interface ILauncherStoreView
{
    bool TryGoBack();
    void Open(Uri uri);
}

/// <summary>A command a provider contributes to the shared library chrome.</summary>
public sealed record LauncherLibraryAction(string Label, string Glyph, Func<Task> Invoke);

public interface ILauncherFrontend
{
    string ProviderId { get; }
    ILauncherSignInView CreateSignInView();
    Type? StorePageType { get; }

    /// <summary>
    /// Provider-owned library actions (import, migration, transfer). The shell renders whatever is
    /// registered, so a provider with no actions contributes no chrome.
    /// </summary>
    IReadOnlyList<LauncherLibraryAction> LibraryActions => [];

    /// <summary>Store mark shown next to a library entry; the shell draws whatever a provider supplies.</summary>
    string? StoreLogo => null;
}

/// <summary>Optional provider-owned account/runtime/game configuration, independent of sign-in.</summary>
public interface ILauncherConfigurationFrontend
{
    FrameworkElement CreateConfigurationView();
}

public sealed class LauncherFrontendRegistry(IEnumerable<ILauncherFrontend> frontends)
{
    private readonly Dictionary<string, ILauncherFrontend> _frontends =
        frontends.ToDictionary(frontend => frontend.ProviderId, StringComparer.Ordinal);

    /// <summary>Every registered frontend, for chrome that any provider may contribute to.</summary>
    public IReadOnlyCollection<ILauncherFrontend> All => _frontends.Values;

    public ILauncherFrontend Get(string providerId) => _frontends.TryGetValue(providerId, out var frontend)
        ? frontend : throw new InvalidOperationException($"Missing frontend for launcher '{providerId}'.");
}
