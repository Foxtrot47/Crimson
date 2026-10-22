# Adding a store provider to Crimson

Crimson is the launcher. All connected store accounts contribute to one library,
search, installed-games navigation and downloads view. There is no global active
store or launcher switch. A game or store result carries its provider identity;
that identity determines which implementation handles it.

Epic is the first production provider. EA and Ubisoft are integration targets,
not advertised as implemented by this refactor. Their protocol code can land in
separate providers without changes to the shared game pages.

## Boundaries

`Crimson.Core/Providers` contains the portable contracts and shared services:

| Contract/service | Responsibility |
| --- | --- |
| `ILauncherProvider` | Stable ID, display name, authentication, library, optional installer/store |
| `ILauncherAuthentication` | Independent account state, validation/refresh and logout; no tokens exposed to the shell |
| `ILauncherLibrary` | Normalized games, DLC relationships, status changes and provider-owned launch lifecycle |
| `ILauncherInstaller` | Supported actions, size estimates in bytes, queue/history, progress, cancellation and optional pause/shortcuts |
| `ILauncherStore` | Public search results and provider-owned product/search URLs |
| `IProviderContentPolicy` | Trusted artwork destinations for the shared Windows shortcut service |
| `LauncherLibrary` | Merge connected accounts, isolate refresh failures and discard results from disconnected sessions |
| `LauncherStores` | Search all available stores concurrently and retain each result's origin |
| `LauncherInstaller` | Combined queue/history presentation and commands routed to the owning installer |
| `LauncherAuthentication` | Per-account connection state and sign-in requests; no application-wide login gate |

The existing Epic protocol engines are named `EpicAuthManager`,
`EpicLibraryManager`, `EpicInstallManager` and `EpicDownloadManager`, and are
located under `Providers/Epic`. The manifest and HTTP repository contract is
`IEpicGamesRepository`. These are Epic implementation details, not interfaces that
EA or Ubisoft must implement. Their namespaces are retained to keep this move
focused. `EpicLauncherProvider` maps Epic metadata into `LauncherGame`, so shared
view models never need Epic assets, namespaces, catalog IDs, image type strings,
exchange codes or manifests.

## Implementing a provider

1. Choose a stable lowercase slug, such as `ea` or `ubisoft`. Register a singleton
   `ILauncherProvider` in the WinUI composition root. Implement the services above
   with your own protocol types and dependencies. Do not add placeholder providers
   to the production registry before their account/library implementation works.
2. Return `LauncherGame` records containing `new GameId(providerId, nativeId)`, a
   title, optional cover/landscape images, DLC status, launch availability and a
   normalized `LauncherInstallation`. Native identifiers are opaque strings;
   identical IDs and titles from different stores remain distinct library entries.
3. Accept and emit **qualified** `AppName` strings in public library/installer
   calls and `InstallItem`s. Decode using `GameId.Parse`; reject foreign provider
   IDs before calling native code. The shared installer copies requests before
   handing them off; the Epic adapter translates them to/from its legacy IDs.
4. Keep account tokens, licenses, HTTP clients and all protocol objects inside
   the provider. Publish authentication state changes. Discard requests from an
   old account after logout/reconnect; the shared library also guards in-flight
   refresh results with a per-provider session generation. A failed store must
   not prevent the others from loading.
5. Return `Installer = null` or `Store = null` when unsupported. Advertise only
   implemented installer actions with `Supports(ActionType)`. Set
   `SupportsPause`, `SupportsShortcuts` and per-game `CanLaunch` truthfully. Shared
   controls disable unsupported actions rather than assuming Epic's features.
   Installer engines own execution/resume state; the shared view merges their
   queues, forwards each job's progress, and routes cancellation/pause by identity.
   Do not assume a process is a complete launch contract: the provider's
   `LaunchApp` owns helper lifetimes, IPC, license acquisition and cleanup.

Register a Windows frontend alongside the provider:

```csharp
services.AddSingleton<ILauncherProvider, EaLauncherProvider>();
services.AddSingleton<ILauncherFrontend, EaFrontend>();
services.AddSingleton<IProviderContentPolicy, EaContentPolicy>(); // if using remote shortcut artwork
```

`ILauncherFrontend.CreateSignInView` returns an `ILauncherSignInView` with its
`FrameworkElement`, initialize and close methods. Settings opens that view in a
per-account dialog. Password/MFA, embedded browser, external browser, device
authorization or a helper process can all be implemented here. Sign-in is not
forced into an Epic exchange-code method. Close must release browser/helper
resources and ignore completions after the view is disposed.

An optional `StorePageType` implements `ILauncherStoreView` to host that store's
browsing, checkout, popups and deep-link handling. It is hosted by the shared
Stores page; browsing it never filters the library. `EpicLoginPage`,
`EpicStorePage` and `EpicFrontend` demonstrate the integration.

## Persistence and compatibility

Use `ProviderDataPaths.ForProvider(crimsonDataRoot, providerId)` for each
provider's credentials, library cache, install state, manifests and support files.
EA and Ubisoft get separate `providers/ea` and `providers/ubisoft` directories.
Encrypt secrets with the platform credential protector and retain each provider's
trust policy. Do not reuse Epic's storage schema or authentication client.

Epic deliberately retains its existing root and JSON schemas, including its
installation resume state, so existing users do not need a destructive migration.
Shared settings remain at Crimson's root. `WebViewEnvironmentFactory.CreateAsync`
requires a provider ID and uses the same isolation rule for browser profiles,
preserving Epic's original profile path.

New install dialogs suggest `<install root>/<provider>/<game title>` so equal
titles from different stores do not overwrite one another. Titles are sanitized
as a single directory component. Existing installed-game paths are unchanged.

For filenames derived from opaque native IDs, use `ProviderDataPaths.GameFileKey`.
Never turn a native ID directly into an unchecked path. The shared Windows
shortcut manager accepts `LauncherGame` and a registered artwork policy. Without
an artwork policy it uses the Crimson icon. Existing Epic shortcut/icon filenames
are preserved; other providers get names and icon keys scoped to their provider.

New shortcuts encode the qualified ID, for example
`--launch-game=ubisoft%3A123` or `crimson://launch?app=ea%3A123`. Existing unqualified
shortcuts resolve specifically to Epic. Unknown qualified provider IDs fail
instead of falling back to Epic. Existing unqualified shortcut ownership is
recognized when updating or removing Epic shortcuts.

## EA and Ubisoft integration notes

The design was checked against the public proof-of-concepts:

- [EA-Desktop-RE](https://github.com/Foxtrot47/EA-Desktop-RE), inspected at
  `72442d23c091d43991665c463203b8320e28111b`: EA sign-in, entitlement/offer IDs,
  package/DiP installation, EA-issued licenses and managed LSX/helper supervision
  belong in the EA provider. Its Python/native dependencies and locally supplied
  EA runtime files are provider prerequisites, not prerequisites of Crimson's
  other accounts.
- [Ubisoft-Connect-RE](https://github.com/Foxtrot47/Ubisoft-Connect-RE), inspected at
  `baebae37e9a7e981ddf7c99ed5fd5a6975ba9699`: password/2FA/remember-me sessions,
  product IDs, Demux manifests/slices, named pipes and runtime supervision belong
  in the Ubisoft provider. Unsupported cloud-save transfer must remain unavailable
  until implemented and validated.

Neither prototype's protocol formats, runtime supervision or sign-in requirements
are approximated by fake Epic models. Registering both real implementations will
add their games to the existing combined library automatically.

## Validation

Run `dotnet test Crimson.Core.Tests/Crimson.Core.Tests.csproj -c Release`.
`LauncherProviderTests` exercises three independent providers with duplicate native
IDs, combined presentation, account isolation, session races, capability checks,
installation routing and shortcut identity. `LauncherStoreTests` covers combined
search, store failure isolation and cancellation. Existing Epic parsing,
authentication, installation and security tests continue to exercise its adapter
dependencies. Windows CI additionally builds WinUI and runs the shortcut/platform
adapter tests.

The first additional provider is [Ubisoft Connect](UBISOFT_INTEGRATION.md): account authentication, shared-library entitlements, explicit installed-game paths, native SDK session pipes, and live Denuvo authentication. Its optional installer/store capabilities remain absent. Frontends can implement `ILauncherConfigurationFrontend` to expose provider-owned configuration from account settings independently of sign-in.
