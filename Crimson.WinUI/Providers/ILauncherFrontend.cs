using System.Collections.Generic;
using System.Linq;
using System;
using Microsoft.UI.Xaml;

namespace Crimson.Providers;

public interface ILauncherSignInView
{
    FrameworkElement Element { get; }
    void InitializeSignIn();
    void CloseSignIn();
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
}

public sealed class LauncherFrontendRegistry(IEnumerable<ILauncherFrontend> frontends)
{
    private readonly Dictionary<string, ILauncherFrontend> _frontends =
        frontends.ToDictionary(frontend => frontend.ProviderId, StringComparer.Ordinal);

    public ILauncherFrontend Get(string providerId) => _frontends.TryGetValue(providerId, out var frontend)
        ? frontend : throw new InvalidOperationException($"Missing frontend for launcher '{providerId}'.");
}
