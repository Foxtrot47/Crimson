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
| The PoC aux init contains fixed WD2 addon IDs. | Generate the addon validation list from owned, released addon records in the ownership catalog. This generalization needs native testing. |
| Runtime/SDK pipe lifetime matters across EAC starter/process handoffs. | Current-user pipes, inherited starter event, persistent connections, process registration and handoff grace periods. One Ubisoft title at a time. |
| Installer-init is a distinct IPC operation. | Answer `f13` initialization through response `f10`, with empty optional chunk catalog and configured language. This does not implement game installation. |
| Permanent cloud-save synchronization and general installer metadata remain incomplete in the PoC. | Leave both capabilities unavailable. Keep writable local saves isolated under Crimson's Ubisoft/account directory. |

The entitlement endpoint supplies ownership and application IDs but often lacks game names/artwork. This version uses returned names when available, otherwise a product-ID label. Registering an installation lets you set its title. It does not parse the official client's configuration/account cache. Package/bundle containers are excluded; games and addons remain represented. DLC parent relationships are not invented.

## Implemented surface

- Password sign-in, authenticator challenge, remember-me renewal, logout, and protected session storage through the existing Windows credential protector. Passwords and MFA challenges are not persisted. API failure bodies and wire/token payloads are not logged.
- Shared-library entitlements, explicit existing-installation registration, game launch, session/installer-init IPC, live PC ticket issuance and Denuvo challenge/answer relay.
- Play-session heartbeat/cleanup, game process tracking, account-disconnect cancellation, and runtime diagnostics in Crimson's log. The configuration view shows the last launch/runtime error when opened.
- A provider-owned configuration view exposed through the optional frontend configuration contract.

Ubisoft's installer and store interfaces are absent, so shared UI cannot advertise unsupported downloads, updates, repairs, moves, uninstalls, or store search. EA is not implemented by this change.

## Your Windows test setup

No runtime is distributed, downloaded, or installed by this branch. Supply your own matching runtime files from your installation:

```
uplay_r1.dll                 uplay_r164.dll
uplay_aux_r1.dll             uplay_aux_r164.dll
UbisoftGameLauncher.exe      UbisoftGameLauncher64.exe
discord-rpc.x86.dll          discord-rpc.x64.dll
steam_api.dll               steam_api64.dll
```

1. Build/run Crimson on Windows. Close Ubisoft Connect so its named pipes do not conflict with Crimson's bridge.
2. Ensure the 32-bit machine key `HKLM\SOFTWARE\Ubisoft\Launcher`, value `InstallDir`, points to the runtime directory you intend to use. On a 64-bit registry viewer this appears under `WOW6432Node`. Crimson only reads this setting and rejects a mismatch; it does not overwrite it. Keep the original value if changing it for your test.
3. In **Settings > Accounts**, connect Ubisoft. Complete the authenticator step if requested. Epic can stay connected.
4. Open Ubisoft **Configure**, set the runtime directory, and choose **Load account games**. Select an owned game, set a title, absolute installation directory, absolute game executable inside that directory, optional arguments, and installed language. Save.
5. Open that game's page in the unified Crimson library and select **Play**. Runtime validation and a refreshed entitlement check happen before startup. The game must have a valid live application ID and be marked playable. EAC is not disabled automatically.
6. Test startup, a Denuvo-protected title, starter/EAC handoffs, gameplay, exit, and a second launch. Check `Settings > Accounts > Configure` and Crimson's logs for failures. Runtime logs include stages, product IDs, activation result names, and exit codes, not credentials or challenge/answer bytes.
7. Disconnect the account and confirm its library disappears. The bridge closes; Crimson does not terminate game processes or delete game files.

State is under `%LOCALAPPDATA%\Crimson\providers\ubisoft\`:

| File/directory | Contents |
| --- | --- |
| `session.dat` | Windows-protected remembered session; deleted on logout. Unremembered sessions stay in memory. |
| `configuration.json` | Runtime directory and explicitly registered installed-game launch options. |
| `savegames\ubisoft-<account hash>\<product ID>\` | Writable local saves, with the required trailing separator in IPC. No automatic migration/upload of existing progress. |
| `statistics\hwscore` | SDK cache destination notice, separate from official client state. |

**Forget installation** removes Crimson's registration only. It keeps game files and saves.

## Verification and remaining work

Portable tests exercise malformed/partial frames, protocol field shapes, asynchronous demux routing, omitted defaults, live-ticket separation, entitlement filtering, protected persistence, MFA/refresh/logout, launch cancellation, generated sessions, installer-init routing, and activation success/error deduplication. All fixtures use synthetic data. The Windows test project also compiles the bridge and exercises native child startup without any Ubisoft runtime or live account.

Native runtime/game validation is intentionally left to you. Full WinUI compilation and Windows adapter tests run in CI. Local Linux checks cover the portable suite and Windows adapter cross-compilation; they cannot establish that a game boots or that Denuvo accepts the C# bridge.

Follow-ups: signed-URL manifest/slice downloader with hash and path validation; full live configuration/launch metadata and artwork; proper install chunks/languages; tested secure-DLC/cache-invalidation IPC; account-safe persistent activation cache with understood expiry/quotas; long-running authentication renewal in the running SDK session; permanent cloud-save conflict handling; friends/achievements/overlay and broader title coverage. Unsupported aux cache-invalidation requests fail explicitly. At most eight distinct activation challenges are accepted per launch. The hardware-score fields retain the PoC's compatibility constants and are not a benchmark of the current machine.
