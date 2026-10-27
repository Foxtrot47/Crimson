# Library and artwork caching

Library ownership and its last successful refresh are saved in `library-cache.json`
under Crimson's application data directory, alongside the existing metadata files.
The snapshot is bound to the Epic account ID and expires after 20 minutes. A fresh
snapshot restores the library without calling Epic. An expired snapshot remains
usable if a refresh fails; the next library request retries. Missing metadata is
retried too. Logout or changing accounts clears ownership and metadata, while
local installation records remain intact.

Ordinary Store browsing does not invalidate the library. Entering Epic checkout
(including checkout frames and popups) invalidates ownership when leaving Store.
An intercepted launcher request still refreshes ownership immediately. These
refreshes reuse unchanged game metadata and only fetch missing or changed entries.
An empty ownership response clears the displayed library.

Library cards retain their objects when their content is unchanged, and the
Library page participates in the Frame's bounded navigation cache. Artwork for
library cards and game details shares a disk cache under `artwork`. Keys hash the
complete source URL, so a changed URL gets a separate entry. Downloads are
coalesced per URL, limited to four at a time, limited to 20 MiB per image, and
published through temporary files. Entries expire after 30 days; successful writes
trim old entries and cap the cache at 256 MiB. A failed refresh can use an existing
image. Public artwork is shared across accounts and contains no authentication
credentials. WinUI decodes local files through streams to support both packaged
and unpackaged application data paths.

## Automated validation

```powershell
dotnet test Crimson.Core.Tests/Crimson.Core.Tests.csproj -c Release
dotnet test Crimson.Tests/Crimson.Tests.csproj -c Release -p:Platform=x64
dotnet build Crimson.WinUI/Crimson.WinUI.csproj -c Release -r win-x64 -p:Platform=x64
```

CI runs the core suite on Linux and Windows, the adapter suite on Windows, and
builds the WinUI frontend. Regression tests cover cache reuse after restart,
account isolation, logout, corruption, offline fallback and retry, incremental
ownership updates, empty libraries, invalidation races, stable cards, artwork
coalescing, URL changes, expiry, failed downloads, obsolete page completions,
and decoding a cached image through Windows' native image decoder.

## Interactive Windows smoke check

1. Sign in and load Library, then open a game's detail page. Let artwork finish.
2. Visit Store without entering checkout, then return to Library. Confirm cards,
   artwork, and scroll position remain, with no new library asset/metadata request.
3. Revisit the game's details, then restart Crimson within 20 minutes. Confirm
   ownership and artwork load from disk. Repeat with networking unavailable after
   reaching the main application; authentication itself still requires network.
4. Acquire a game through checkout (also try a checkout popup), return to Library,
   and confirm the new title appears without downloading all existing metadata.
5. Sign out and sign into another account. Confirm the previous account's library
   is absent and local installations still have their correct status.

Automated tests do not replace a live Epic checkout or interactive WebView2 check.
