# Ubisoft integration: initial launch and activation implementation

Crimson registers Ubisoft alongside Epic. Accounts remain independent and games appear in the same library, with IDs such as `ubisoft:2688`. There is no active-store switch.

## PoC review

Reviewed [Foxtrot47/Ubisoft-Connect-RE](https://github.com/Foxtrot47/Ubisoft-Connect-RE/tree/9313b1ecd6154e6a8edbc4448bba33cea91c0b36), particularly `poc/ubisoft_gui.py`, `poc/fake_launcher.py`, the independent runner, protocol descriptors, tests, and docs 02, 03, 05, 06, 08, 09, and 10.

The current Python implementation goes substantially beyond the older capture-based proof of concept. The repository reports independent Watch Dogs 2 gameplay with EAC enabled and server-issued PC/Denuvo tickets. That is evidence for its tested title and machine; Crimson's C# implementation still needs native testing.

Findings that determine this implementation:

| Finding | Crimson implementation |
| --- | --- |
| Fresh launch has a working generated-message path; older helpers still fall back to capture files. Some module comments describe the older behavior. | Use protocol facts from the independent/fresh path. No capture files, replay modes, activation files, Python dependency, or native runtime binaries are copied into Crimson. |
| A login ticket, ownership token, PC ticket, and Denuvo answer are different credentials. | Ownership `f14`/response `f13` supplies session `f8`. Ownership `f28`/response `f27` supplies the token for `denuvo_service`, whose request `f2`/response `f3.f1` supplies the aux answer. |
| Demux API 13389, varint ownership fields, proto3 defaults, dynamic connection IDs, and request matching matter. | Bounded protobuf/frame codec; TLS certificate/hostname validation; matching by connection and request ID, including omitted zero IDs. |
| The Denuvo service expects the game challenge in its encoded form. | Relay its bytes unchanged; no base64 decoding or local token generation. Ubisoft validates ownership and issues the answer. |
| Issuance can consume activation quota, including requests whose replies time out. | No automatic network retry. Identical challenges share an in-memory task, including failed tasks, for that launch. A new launch starts fresh. No persistent/offline activation cache. |
| Session application IDs must come from current entitlements. The session product list uses the ownership catalog's filters/order. | Refuse launch without a valid entitlement application ID; use the live ownership catalog for session products. |
| The PoC aux init contains fixed WD2 addon IDs. | Accept optional per-game addon IDs in configuration and validate them against the live owned/released addon catalog. Do not send unrelated addons from the account or assume WD2 IDs apply to other titles. The default list is empty; DLC behavior needs native testing. |
| Runtime/SDK pipe lifetime matters across EAC starter/process handoffs. | Current-user pipes, inherited starter event, persistent connections, process registration and handoff grace periods. One Ubisoft title at a time. |
| Installer-init is a distinct IPC operation. | Answer `f13` initialization through response `f10`, with empty optional chunk catalog and configured language. This does not implement game installation. |
| Permanent cloud-save synchronization and general installer metadata remain incomplete in the PoC. | Leave both capabilities unavailable. Keep writable local saves isolated under Crimson's Ubisoft/account directory. |

The entitlement endpoint supplies ownership and application IDs but not display data. Names and
artwork are read from the installed Ubisoft Connect client's
`%LOCALAPPDATA%\Ubisoft Game Launcher\cache\configuration\configurations` cache: per-game
`root.name` (quoted, bare, or an `lN` key resolved through `localizations.default`), the image
hashes served from `https://static3.cdn.ubi.com/orbit/uplay_launcher_3_0/assets/`, and the
`addons:` list that supplies addon names and their owning game. Anything still unnamed falls back
to `GET /v1/applications?applicationIds=...` once, and the result is cached in
`providers/ubisoft/metadata.json` (public data, shared across accounts, re-read when the client
cache changes). Nothing is invented: titles no source describes stay labelled by product ID.
Registering an installation can still override a title. Package/bundle containers are excluded;
games and addons remain represented, and addon-to-game parents come only from the client cache.

## Implemented surface

- Password sign-in, authenticator challenge, remember-me renewal, logout, and protected session storage through the existing Windows credential protector. Passwords and MFA challenges are not persisted. API failure bodies and wire/token payloads are not logged.
- Shared-library entitlements, explicit existing-installation registration, game launch, session/installer-init IPC, live PC ticket issuance and Denuvo challenge/answer relay.
- Play-session heartbeat/cleanup, game process tracking, account-disconnect cancellation, and runtime diagnostics in Crimson's log. The configuration view shows the last launch/runtime error when opened.
- A provider-owned configuration view exposed through the optional frontend configuration contract.

Ubisoft's installer and store interfaces are absent, so shared UI cannot advertise unsupported downloads, updates, repairs, moves, uninstalls, or store search. EA is not implemented by this change.

## Runtime and installed games

Nothing is registered by hand. Crimson reads what Ubisoft Connect writes:

| What | Where it comes from |
| --- | --- |
| Runtime directory | `HKLM\SOFTWARE\Ubisoft\Launcher` value `InstallDir`, both registry views |
| Game executable | the client configuration cache's first `start_game.online.executables` entry (`path.relative`, or the value `path.register` names) |
| Install directory | the `working_directory.register` key that entry names, otherwise `HKLM\SOFTWARE\Ubisoft\Launcher\Installs\<product>\InstallDir` |
| Account identity | `GET /v3/users/me` through the session; shown on the Accounts row |

A game is launchable when it is owned, playable, carries a live application id, and the client has
it installed. When Ubisoft Connect is absent, the library still lists owned games with names and
artwork and Play reports the missing runtime instead of asking for a path. Entries in
`configuration.json` stay available as overrides (title, arguments, language, addon validation ids)
but are optional; the manual configuration UI is gone because a normal installation needs nothing
entered. Installed state is re-read on every library refresh, so installing or removing a game in
Ubisoft Connect shows up the next time Crimson refreshes accounts. Crimson does not download games:
Ubisoft's installer and store interfaces remain unimplemented.

## Testing on Windows

1. Install Ubisoft Connect and the games you want to test, or let the client register an existing
   installation. Close Ubisoft Connect so its named pipes do not conflict with Crimson's bridge.
2. In **Settings > Accounts**, connect Ubisoft. Complete the authenticator step if requested. The row
   shows the signed-in account name; Epic stays independent in its own row.
3. Owned games appear in the unified library. Ones the client has installed show Play.
4. Test startup, a Denuvo-protected title, starter/EAC handoffs, gameplay, exit, and a second launch.
   Failures appear on the game page and in Crimson's log, which records stages, product ids,
   activation result names and exit codes, never credentials or challenge/answer bytes.
5. Disconnect the account and confirm its library disappears. The bridge closes; Crimson does not
   terminate game processes or delete game files.
State is under `%LOCALAPPDATA%\Crimson\providers\ubisoft\`:

| File/directory | Contents |
| --- | --- |
| `session.dat` | Windows-protected remembered session and the sign-in email, deleted only when you disconnect. A rejected or expired ticket keeps the file so the account stays known. |
| `signin.dat` | Sign-in kept only when **Keep me signed in** is on, encrypted for the current Windows user, deleted on disconnect. Renews the session without asking again. |
| `configuration.json` | Optional overrides: runtime directory and per-game launch options. Discovery works without it. |
| `metadata.json` | Cached product titles resolved through the applications endpoint, plus the client-cache stamp that produced them. Names and artwork themselves stay in the official client's cache. |
| `savegames\ubisoft-<account hash>\<product ID>\` | Writable local saves, with the required trailing separator in IPC. No automatic migration/upload of existing progress. |
| `statistics\hwscore` | SDK cache destination notice, separate from official client state. |

**Forget installation** removes Crimson's registration only. It keeps game files and saves.

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

## Verification and remaining work

Portable tests exercise malformed/partial frames, protocol field shapes, asynchronous demux routing, omitted defaults, live-ticket separation, entitlement filtering, protected persistence, MFA/refresh/logout, launch cancellation, generated sessions, installer-init routing, and activation success/error deduplication. All fixtures use synthetic data. The Windows test project also compiles the bridge and exercises native child startup without any Ubisoft runtime or live account.

Native runtime/game validation is intentionally left to you. Full WinUI compilation and Windows adapter tests run in CI. Local Linux checks cover the portable suite and Windows adapter cross-compilation; they cannot establish that a game boots or that Denuvo accepts the C# bridge.

Follow-ups: signed-URL manifest/slice downloader with hash and path validation; full live configuration/launch metadata and artwork; proper install chunks/languages; tested secure-DLC/cache-invalidation IPC; account-safe persistent activation cache with understood expiry/quotas; long-running authentication renewal in the running SDK session; permanent cloud-save conflict handling; friends/achievements/overlay and broader title coverage. Unsupported aux cache-invalidation requests fail explicitly. At most eight distinct activation challenges are accepted per launch. The hardware-score fields retain the PoC's compatibility constants and are not a benchmark of the current machine.
