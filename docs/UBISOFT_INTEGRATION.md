# Ubisoft Connect integration

Crimson registers Ubisoft Connect alongside Epic. Accounts are independent and games share one
library, with IDs such as `ubisoft:2688`. The provider runs on the same Demux services the official
client uses; no Ubisoft Connect installation is required.

Protocol facts come from [Foxtrot47/Ubisoft-Connect-RE](https://github.com/Foxtrot47/Ubisoft-Connect-RE)
(`poc/ubisoft_gui.py`, `poc/fake_launcher.py`, docs 01-11). The field numbers follow the public
protobuf schemas (`demux.proto`, `ownership.proto`, `download_service.proto`, `download.proto`,
`cloudsave_service.proto`).

## Demux

`UbisoftDemuxConnection` opens TLS 1.2 to `dmx.upc.ubisoft.com:443`. The server drops TLS 1.3. It
then sends `push.client_version = 13389` and authenticates with the login ticket (`client_id =
"uplay_pc"`). The legacy catalog handshake is not needed. One reader loop correlates top-level replies by
`request_id`, routes `push.data` to channels by connection ID, and ignores keep-alives.

`OpenAsync(service)` returns a `UbisoftDemuxChannel`. Each channel numbers its own requests from 1,
because proto3 services omit a zero ID.

| Failure | `UbisoftDemuxFailure` |
| --- | --- |
| expired/rejected ticket, banned account, refused service | `Expired`, `Banned`, `Rejected` |
| socket closed, `connection_closed` push | `Closed` (`Banned` for error code 3) |
| no reply in time (30 s default; 60 s for ownership Initialize and Denuvo) | `Timeout` |

| Service | Use |
| --- | --- |
| `ownership_service` | `Initialize` returns every product of the account (about 450 KB) with its full YAML configuration, latest manifest, download ID, branches, associations, ownership, type and state. It also issues ownership tokens (`f28` → `f27`), uplay_pc tickets and session product lists. CD keys (`f2`) and encryption keys (`f24`) are stripped as soon as the reply is parsed. |
| `download_service` | Initialised with an ownership token. Returns signed CDN URLs for `manifests/<id>.manifest`, `.metadata`, `.licenses` and `slices_v3/<shard>/<SHA1>`, one URL list per path in request order, at most 64 paths per request. |
| `denuvo_service` | Relays the game's encoded challenge and returns Ubisoft's answer. There is no automatic retry; identical challenges share one task per launch. |
| `cloudsave_service` | Signs PUT/GET/DELETE requests for save objects. |

The flow-controlled configuration catalog stream, `catalog.bin`, `manifests.json`, `metadata.json`
and REST entitlements are gone. They are deleted on the first refresh.

## Library

One refresh runs one Demux connection and an ownership `Initialize`. The result goes to
`UbisoftOwnershipCache` (`ownership.json`, per account, no secrets), which projects owned products of
type 0 (game) and 1 (add-on). Types 5 (package/region pack) and 11 (bundle) stay hidden but count
as owned for content gating.

Titles, descriptions and artwork come from each product's configuration (`UbisoftProductConfig`):
- names are localised through `localizations.<locale>` and then `default`;
- the locale is matched exactly first, then by language prefix, following the app's UI language;
- images are served from `https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/`.

A cold start uses the cache.

| Product | Library state |
| --- | --- |
| Owned, state Released (3) / PreReleased (1) / Downloadable (2), has a latest manifest | installable |
| Expired (4), unavailable (0), no manifest, or bound to another store (`third_party_platform`) | listed, not installable |
| Installed manifest ≠ latest manifest | `NeedUpdate` |

## Installer

`UbisoftInstallManager` implements `ILauncherInstaller`, `IInstallLanguageSource` and
`IInstallImportSource`. It supports install, update, verify, repair, language change, import, move
and uninstall. Installs are recorded in `installed.json` (`UbisoftInstalledStore`). The legacy
`configuration.json` games are migrated into it once.

**Manifests.** The container is `u32 version | u32 signature length | u32 body length | signature |
zlib(body)`, and inflation is capped at 256 MB. Each chunk can carry:
- `type`: Required or Optional;
- `language`;
- `uplayIds`: an ownership gate, for example an ultra-texture DLC or a region pack.

Each file lists its slices. Every slice has:
- an uncompressed size;
- a download size;
- `downloadSha1`, which names the CDN object and hashes the downloaded bytes;
- an offset;
- a content SHA-1 of the decoded bytes, used to verify.

The shard is `"0123456789abcdefghijklmnopqrstuv"[sha1[0] % 32]`. Compression is per manifest:
Deflate (a zlib stream, e.g. Watch Dogs 2), Zstd, or none. A slice whose downloaded length already
equals its size is stored as-is.

**Selection.** A chunk is installed when both conditions hold:
- its `uplayIds` are empty or intersect the owned set;
- its language is empty or one of the selected languages.

The owned set is every owned product ID and uplay ID of the account, plus the product's
associations. Watch Dogs 2's language chunks are gated on region packs.

Paths are normalised and validated against traversal. Identical duplicates merge; conflicting ones
are rejected. Before the multi-megabyte manifest is downloaded, `.metadata` supplies size estimates
and the language list.

**Languages.** The install dialog and game properties offer the manifest's languages. The default is:
1. the UI language;
2. the same language prefix;
3. `en-US`;
4. the first offered language.

A language change is a Repair carrying `InstallItem.Languages`. It downloads the added chunks and
deletes only the files that just the removed languages used.

**Downloads.** `UbisoftDownloadEngine` signs slice paths in batches of 64 ahead of 16 concurrent
fetches. Each slice is fetched, then:
1. `downloadSha1` is checked against the downloaded bytes;
2. the slice is decoded;
3. the content SHA-1 is checked;
4. it is written at its offset into a preallocated file.

On a 403 or once the URLs have expired, the slices are signed again. A dropped Demux connection is
reopened up to three times. Signed URLs are sent byte for byte (`UbisoftCdnConnection`), because a
normalised `%2f` breaks the signature.

Progress is saved in `<install>/.Crimson/ubisoft-progress.json`:
- pause stops issuing new fetches;
- resume skips completed files;
- cancelling a fresh install deletes only the files that job created.

Transfer samples feed the Downloads page and graph exactly as Epic's do.

| Operation | Behaviour |
| --- | --- |
| Install | The product must be installable. The folder must be empty, or contain only `.Crimson`, unless the same product is being resumed. |
| Update | Downloads files whose slice list changed. Deletes files that left the selection, and nothing else. |
| Verify | Hashes every selected file's slices against their content SHA-1 (size only when a manifest has none). |
| Repair | Verify, then download files that failed, then re-apply pending system setup. |
| Import | Candidates come from `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir` or a picked folder. Sizes are compared with the manifest selection. The install is recorded, and a Verify is queued on a mismatch. |
| Move | `Directory.Move` on the same volume; otherwise copy, verify, delete. The folder keeps its name in the new parent. The registry is rewritten. |
| Uninstall | Deletes the selection's files, `.Crimson`, empty folders and the runtime DLLs Crimson copied. Registry keys are removed through one elevated setup. With the last Ubisoft game, `Launcher\InstallDir` is removed too, but only while it still points at a Crimson runtime folder. User files stay. The manifest's `uninstallRuns` (the official client's uninstallers) are not run. |

## System setup (one UAC prompt per operation)

A game expects the registry entries and prerequisites the official client creates. After an install,
import, move or repair, one `UbisoftSystemSetupRequest` collects:
- the manifest's `installRegistry` entries (both views, `$INSTALL_DIR` substituted, the per-language
  value for the selected language);
- `Launcher\Installs\<id>\InstallDir` (32-bit view, forward slashes, trailing slash) and its `Language`;
- every `installRuns` entry (VC++ runtimes, DirectX, EasyAntiCheat) whose `identifier:version` has not
  completed yet.

`Launcher\InstallDir` tells the game's runtime loaders where the runtime lives. Every loader checked
(`uplay_r1_loader64.dll`, `upc_r2_loader64.dll`; the 32-bit builds read their own view anyway) opens it with
`KEY_WOW64_32KEY`, so Crimson reads and writes only the 32-bit view. It is not written at install time:
Ubisoft Connect rewrites it whenever it runs, so every launch checks it and, when it does not point at
Crimson's runtime folder, sets it in the same prompt as any pending setup. A launch with nothing to change
shows no prompt.

`ElevationNotice` shows an overlay that lists what the request changes
(`UbisoftSystemSetupRequest.Describe`) and opens the UAC prompt itself: after a 3-second countdown the
first time, or on **Continue**. The overlay stays open until the prompt is approved. If the prompt is
declined or fails to open, the overlay says so and offers **Try again**. **Cancel** or Escape closes it
with the setup not applied. The overlay is a popup, not a ContentDialog, so it can appear over the game
properties dialog that Move, Repair and Uninstall start from.

The WinUI `UbisoftSystemSetup` then writes the request to a temporary file and starts
`Crimson.exe --ubisoft-system-setup <request> <result>` with `runas`. `Program.Main` handles that
argument before any window or single-instance logic.

The elevated helper re-validates the request:
- it accepts only HKLM `SOFTWARE\Ubisoft\…` and `SOFTWARE\WOW6432Node\Ubisoft\…` keys;
- it runs only executables inside an install folder the same request registers.

If setup is never approved, an install still finishes with `SystemSetupPending`, and a non-modal notice says
so. The next launch or repair offers the prompt again. A launch is not started while its setup is pending:
without the runtime folder value or the game's registry keys the game fails on its own, so Crimson reports
`UbisoftLaunchNeedsAdministrator` instead. The request file sits in the user's temp folder,
so a process running as the same user could alter it before the helper reads it. The helper's
validation bounds what such a change can do.

## Launch

`UbisoftLaunchPlan` takes the first `start_game.online` executable, or else the first `offline` one,
from the product configuration. The path is either relative to the install folder or read from the
`register` key it names, falling back to the install folder. The working directory and arguments are
resolved the same way.

Bundled runtime DLLs (`Crimson.WinUI/Assets/UbisoftRuntime`) are copied beside the game and recorded
in `.Crimson/ubisoft-runtime.json`: `uplay_r1*.dll` and `uplay_aux_r1*.dll` for the R1 SDK, and
`uplay_r2*.dll` for the R2 SDK (`upc_r2_loader64.dll`, e.g. Trackmania). A game counts as using the modern
runtime when a bounded search of the install tree finds `uplay_r1_loader*`, `uplay_r2_loader*`,
`upc_r1_loader*` or `upc_r2_loader*`.

The game session uses named pipes, a session generated from the ownership product list, a live
uplay_pc ticket and the Denuvo relay. One Ubisoft title runs at a time. R2 games hold about 20
`uplay_ipc` connections open at once and request the session about ten times. They fail on any request
left unanswered, and when a subsystem they asked for in the init request is missing from the session.
So the session includes, when requested:

- friends (empty list);
- one installed content chunk;
- the multiplayer session;
- the product list.

It also carries each consumable's balance. Requests are answered as follows:

- **Friends:** acknowledged with the matching request ID.
- **Achievements:** a list request returns an empty list; image and unlock requests are answered "unavailable" rather than reported as successful.
- **Product list:** returns the game, its owned released add-ons and its consumables. It is answered only for the signed-in account.

## Cloud saves

These apply to products whose configuration says `cloud_saves: yes`. Local saves live in
`savegames/<account>/<product>/<slot>.save`; the runtime creates `<slot>.save.upload` when the game
writes a save.

| When | What happens |
| --- | --- |
| Before launch | `GET /v1/profiles/<profile>/entities?name=upcCloudSaveUplayId_<product>`. Slots whose cloud hash or time differs from the last sync are downloaded, unless the local slot is dirty. If the local slot is dirty and the cloud copy changed, the shared conflict dialog decides. |
| After exit | Each dirty slot is compressed (zlib) and `PUT` to a URL signed by `cloudsave_service`, with `Content-MD5`. The entity is then `PUT` with revision + 1, slot hash = lowercase MD5 hex of the uncompressed save, size, time and PC name. The flags are cleared. |

Before any local save is overwritten, a backup is written to
`savegames-backup/<product>/<timestamp>/`. Sync state is kept in `cloudsaves/<product>.json`.
Storage is written before the entity, so the entity never points at missing bytes; the official client
uses the opposite order. The exact revision and size semantics of the entity have not been confirmed
against the live service.

## State

Everything is under `%LOCALAPPDATA%\Crimson\providers\ubisoft\`:

| File/directory | Contents |
| --- | --- |
| `session.dat` | Windows-protected session and sign-in email; deleted on disconnect |
| `signin.dat` | Remembered sign-in (**Keep me signed in** only), encrypted for the current Windows user |
| `ownership.json` | The account's ownership list with configurations and manifest IDs; no CD keys or encryption keys |
| `installed.json` | Installed products: folder, manifest, languages, chunks, completed prerequisites, pending setup |
| `cloudsaves/<product>.json` | Last-synced cloud slot state |
| `savegames/…`, `savegames-backup/…` | Local saves and backups taken before a cloud download replaced them |

Per installation, `<install>/.Crimson/` holds the resume file and the record of copied runtime DLLs.

## Session lifetime

A Ubisoft session is a rolling window, not a fixed token. Re-presenting the current ticket to
`POST /v3/profiles/sessions` returns a new ticket whose expiry has moved forward by the elapsed
time (measured: +66 s after 65 s idle), so a client that extends periodically never lapses.
The official client does exactly that - `JobExtendSession` with `ExtendSessionMode: Periodic`
and `OnlyOnce`, discovered in the shipped binaries.

Crimson therefore keeps a session alive the same way:

| Situation | Behaviour |
| --- | --- |
| Session valid on launch | extended immediately (`Ubi_v1 t=<ticket>` + `Ubi-SessionId`), new expiry stored |
| Running | a loop every 10 minutes extends once less than 30 minutes remain |
| Closed for longer than the window | the ticket is refused as `Ticket is expired` and cannot be extended; the remembered sign-in is replayed once |
| Replay refused | the dialog opens with the address filled in |

Extension is the primary path, which is why a running Crimson no longer asks for the password.
The stored sign-in in `signin.dat` remains the fallback for a long absence - the same trade the
official client makes with `ConnectSecureStorage.dat`. Disconnect stops the loop and deletes both
files.

`connect.ubisoft.com/api/sso/v1/refresh` is a different thing: it exchanges a valid session for a
web `ssoId` used by `ssoSet.html` for the store, and does not renew the services session.

## Verification

Portable tests use synthetic data and cover:
- Demux framing, handshake bytes, request correlation, timeouts and closes;
- ownership parsing with secrets removed, and download URL batching;
- manifest, metadata and configuration parsing, chunk gating, and the slice codec;
- the install engine: offsets, mixed compression, resume, cancel, update deletions, verify, repair,
  language changes and uninstall safety;
- system-setup policy validation;
- library projection, launch-plan resolution, the runtime probe and cloud-save sync.

A live check against this account (November 2026), run from a temporary test harness:
- the library refresh listed 24 games;
- language lists and size estimates for Watch Dogs 2 were correct (en-US only: 32.1 GB download,
  34.5 GB on disk);
- Rabbids Coding! (1.23 GB) installed in 47 s. Verify then passed, caught a corrupted file, and passed
  again after the repair;
- uninstall left a user file in place.

UAC-elevated setup and in-game launch and saves still need native testing per title.
