using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Crimson.Providers;

/// <summary>
/// Space a provider asks for when its sign-in view is hosted in a dialog. A null height lets a form
/// size to its content; a browser page has no natural height and needs one.
/// </summary>
public sealed record LauncherSignInSize(double Width, double? Height);

public interface ILauncherSignInView
{
    FrameworkElement Element { get; }
    void InitializeSignIn();
    void CloseSignIn();

    /// <summary>Browser-based flows need a page; forms need much less. Defaults to the browser size.</summary>
    LauncherSignInSize PreferredSize => new(400, 680);
}

public interface ILauncherStoreView
{
    bool TryGoBack();
    void Open(Uri uri);
}

public interface ILauncherFrontend
{
    string ProviderId { get; }
    ILauncherSignInView CreateSignInView();
    Type? StorePageType { get; }

    /// <summary>
    /// Opens the provider's import of games it finds already installed, or null when the store has none.
    /// The shell lists every store that supplies one behind a single Import button.
    /// </summary>
    Func<Task>? Import => null;

    /// <summary>Store mark shown next to a library entry; the shell draws whatever a provider supplies.</summary>
    string? StoreLogo => null;

    /// <summary>Fill behind <see cref="StoreLogo"/> on account surfaces, so the mark keeps its brand contrast.</summary>
    Color? StoreTileColor => null;
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
