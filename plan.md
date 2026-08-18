# Crimson Modernization Plan

## Objective

Turn Crimson into a production-grade, cross-platform Epic Games launcher with:

- A portable, testable launcher engine targeting .NET 10.
- Reliable and recoverable install, update, repair, move, and uninstall operations.
- A correct library and manifest identity model.
- Secure authentication, networking, credential storage, and logging.
- Framework-neutral viewmodels using CommunityToolkit.Mvvm.
- A maintained WinUI frontend during the migration.
- A future Avalonia frontend for Windows and Linux.
- Linux game launching through Proton using Epic's Windows payloads.

This is an incremental migration, not a big-bang rewrite. Every completed phase must leave the existing Windows application buildable and usable.

## Historical baseline at plan creation

When this plan was created:

- The application targets `.NET 8` and WinUI 3 on Windows x64.
- The deterministic test suite has 50 passing tests.
- Release build and self-contained Windows x64 publish succeed.
- A complete live Among Us lifecycle has been exercised: install, verify, restart, launch, repair, and uninstall.
- Manifest path containment, bounded downloads, atomic download publication, exact chunk reads, update classification, and empty-directory uninstall cleanup have been added.
- The principal remaining risks are installer lifecycle concurrency, non-transactional updates, persistence, parser bounds, network/authentication security, library refresh concurrency, and platform coupling.

## Current implementation status

This status reconciles the roadmap with the repository as of 2026-08-18. The task checklists remain acceptance criteria; this table is the authority for phase progress until each checklist is audited and checked off.

| Phase | Status | Evidence or remaining gate |
| --- | --- | --- |
| 0 — Baseline | Complete | Synthetic lifecycle fixture, manager characterization, deterministic launch coverage, Windows build/publish, and a full Among Us lifecycle pass. |
| 1 — .NET 10 | Complete | Application, tests, CI, build, and self-contained publish target .NET 10. |
| 2 — Security containment | Complete | Containment code and adversary tests pass; a fresh WinUI exchange-code login created and validated a new persisted session. |
| 3 — Portable boundaries | Complete | Core, Infrastructure, and portable tests use project references and build under plain `net10.0`. |
| 4 — Network/parser hardening | Complete | Deterministic malformed-input budgets and all portable projects passed on Ubuntu CI; Windows tests and publish also passed. |
| 5 — Durable JSON state | Complete | Bounded typed schemas, idempotent legacy migration, single state owners, stable snapshots, corruption quarantine, revisioned atomic journals, and full transition recovery passed locally and in Windows/Ubuntu CI. |
| 6 — Filesystem semantics | Complete | Typed manifest paths, cross-host collision rules, link rejection, unique Linux import resolution, local-filesystem policy, capacity and volume gates, mutation-time revalidation, and concrete Windows adapters pass locally on Windows and WSL Ubuntu 24.04. |
| 7 — Library service | Complete | The authoritative portable service publishes immutable sequenced snapshots, serializes refreshes, atomically applies metadata, classifies updates by comparable manifest identity, and produces platform-neutral launch plans; WinUI uses a temporary compatibility facade. |
| 8 — Transactional installer | Partial | Recoverable update publication exists; the single coordinator, durable pause/resume, and complete fault matrix remain. |
| 9 — Shared MVVM | Parked partial work | Existing Presentation code is retained but does not satisfy the phase until WinUI uses the shared workflows. |
| 10 — Linux headless/Proton | Not started | No Linux platform project or headless host exists. |
| 11 — Avalonia | Parked prototype | The existing host remains buildable but is not parity evidence and receives no feature work before Phase 10 passes. |
| 12 — Release hardening | Not started | Release gates remain unchanged. |

Current deterministic Windows results: Core 81 passed with two opt-in local/live tests skipped; Infrastructure 9 passed; Presentation 4 passed; Windows 60 passed with one opt-in live lifecycle skipped; Avalonia Release build and WinUI self-contained publish passed. WSL Ubuntu 24.04 results: Core 80 passed with three local-manifest tests skipped; Infrastructure 9 passed; Presentation 4 passed; Avalonia Release build passed. The full Among Us live lifecycle passed previously, and the Rocket League build/manifest baseline is recorded.

Premature Presentation and Avalonia code is preserved to avoid waste, but parked code does not advance a phase or relax an exit gate.

## Architectural decisions

1. **Target .NET 10.**
   - Portable projects target `net10.0`.
   - Windows platform and WinUI projects target `net10.0-windows10.0.22621.0` or the minimum Windows TFM required by the selected compatible Windows App SDK.
   - Do not multi-target .NET 8 unless a concrete compatibility requirement appears.

2. **Keep WinUI operational during the migration.**
   - Existing visual layout and user workflows remain stable.
   - Small UI changes are permitted only for security, lifecycle correctness, or platform adapters.
   - Avalonia feature work begins only after the engine, persistence, Linux headless host, and shared presentation layer are stable.

3. **Use versioned atomic JSON for mutable application state.**
   - JSON state writes use temporary files, durable flushes, atomic replacement where supported, and validated backup recovery.
   - State schemas are versioned and migrated without silently discarding legacy installations.
   - Manifests, chunks, staging files, backups, and game files remain filesystem objects.
   - Credentials remain in operating-system credential storage.

4. **Linux initially runs Epic Windows payloads through Proton.**
   - Payload selection is explicit and independent of the host OS.
   - Initial Proton support discovers user-installed Steam Proton, Proton-GE, `compatibilitytools.d`, and configured custom installations.
   - Downloading or managing Proton distributions is deferred.

5. **Preserve user data and existing installations.**
   - Legacy JSON is migrated, not silently discarded.
   - Untracked files inside game directories are preserved.
   - A failed update must recover to a complete old or complete new installation before launch is allowed.

6. **Keep dependency direction inward.**
   - Platform hosts depend on shared presentation and application contracts.
   - Core and presentation code never depend on WinUI, Avalonia, WebView2, DPAPI, Proton, or other platform UI/runtime implementations.

7. **Reject link-based install trees instead of supporting them.**
   - Manifest symlink entries are unsupported and rejected before file mutation begins.
   - Install roots and existing descendants used by mutations must not be symbolic links, junctions, or reparse points.
   - Containment and link state are revalidated immediately before each mutation.
   - Native no-follow syscalls are deferred unless Crimson later introduces an elevated or shared installation service. This policy protects against accidental redirection but does not claim kernel-enforced race freedom against a malicious same-user process.

## Target project structure

Start with a small number of clear boundaries. Split further only when a project has a distinct reason to change or ship independently.

```text
Crimson.Core                         net10.0
  Domain records and value types
  Epic manifest/chunk parsing
  Install/update planning
  Installer state machine and service contracts
  Library application logic and service contracts

Crimson.Infrastructure               net10.0
  Epic HTTP repository
  Versioned atomic JSON stores and migrations
  Portable download/hash implementations
  Manifest cache
  Portable filesystem primitives

Crimson.Presentation                 net10.0
  Shared CommunityToolkit.Mvvm viewmodels
  Presentation DTOs
  Framework-neutral interaction contracts

Crimson.Platform.Windows             net10.0-windows
  Windows credential storage
  Direct Windows process runner
  Windows filesystem capabilities and directories

Crimson.Platform.Linux               net10.0
  XDG directories
  Secret Service credential storage
  Linux filesystem capabilities
  Proton discovery, profiles, and process runner

Crimson.WinUI                        net10.0-windows
  Existing XAML and views
  WebView2, tray, Mica, native dialogs and pickers
  WinUI adapters and composition root

Crimson.Avalonia                     net10.0
  Parked prototype until Phase 11
  Future parity views and Windows/Linux composition roots

Crimson.Core.Tests                   net10.0
Crimson.Infrastructure.Tests         net10.0
Crimson.Presentation.Tests           net10.0
Crimson.Platform.Tests               platform-specific when required
```

Dependency direction:

```text
Crimson.WinUI ──────┐
                    ├──> Crimson.Presentation ──> Crimson.Core
Crimson.Avalonia ───┘

Crimson.WinUI/Avalonia ──> Crimson.Infrastructure
Crimson.WinUI/Avalonia ──> selected platform adapter
Crimson.Infrastructure ──> Crimson.Core contracts
```

`Crimson.Core` and `Crimson.Presentation` must contain no references to:

- `Microsoft.UI`
- Avalonia packages
- `Windows.*`
- WinRT
- WebView2
- `BitmapImage`
- Native windows, controls, brushes, visibility types, or dialogs
- `App.GetService`
- DPAPI or Secret Service implementations
- Proton implementations

---

# Ordered roadmap

Follow the current status table and close the earliest unmet exit gate before starting new behavioral work. Existing premature code may remain buildable while parked, but it must not receive feature work or be counted as phase completion. Within a phase, integrate shared behavior changes serially.

## Phase 0 — Preserve the known-good baseline

### Tasks

- [x] Commit or otherwise checkpoint the currently verified source and tests.
- [x] Record the current build, test, and publish commands.
- [x] Add a checked-in, redistributable synthetic game fixture containing:
  - [x] Multiple files.
  - [x] Shared chunks.
  - [x] Multiple chunk parts per file.
  - [x] An empty file.
  - [x] A harmless executable stub or launch fixture.
  - [x] Old and new manifests with unchanged, changed, added, and removed files.
  - [x] Expected hashes and sizes.
- [x] Add characterization tests around the current public `InstallManager` and `LibraryManager` behavior.
- [x] Record expected event ordering and terminal states for every action.
- [x] Document recovery invariants and supported current behavior.

### Required characterization coverage

- Install
- Update
- Repair
- Import
- Move
- Pause/resume
- Cancellation
- Uninstall
- Queue/history behavior
- Library refresh
- Update detection
- Process launch planning

### Exit gate

- Existing tests remain green.
- The fixture runs without developer-local Epic cache files.
- Current manager-facing behavior and event ordering are documented.
- WinUI build and publish remain green.

### UI impact

None.

---

## Phase 1 — Upgrade the existing application to .NET 10

Keep the runtime upgrade separate from architectural refactoring.

### Tasks

- [x] Inventory installed .NET SDKs and select the current supported .NET 10 SDK patch.
- [x] Add `global.json` with an intentional SDK roll-forward policy.
- [x] Upgrade Windows App SDK and related WinUI packages to versions compatible with .NET 10.
- [x] Upgrade the existing application TFM to `net10.0-windows...`.
- [x] Upgrade the current tests to .NET 10.
- [x] Change CI to install `10.0.x`.
- [x] Restore, test, build, and publish from a clean checkout.
- [x] Record any changed runtime, trimming, AOT, or packaging warnings.

### Exit gate

- All baseline tests pass on .NET 10.
- Release WinUI build succeeds.
- Self-contained Windows x64 publish succeeds.
- No behavioral refactoring is mixed into the runtime upgrade.

### UI impact

None.

---

## Phase 2 — Emergency security containment

These issues are addressed before broad code movement because they involve credentials, remote input, and filesystem paths.

### Tasks

- [x] Replace shared `HttpClient.DefaultRequestHeaders.Authorization` mutation with request-scoped authorization headers.
- [x] Introduce separate named clients for OAuth, Epic API/catalog, manifests, and chunks.
- [x] Remove logging of:
  - [x] WebView messages.
  - [x] Exchange codes.
  - [x] Access and refresh tokens.
  - [x] Signed URL query strings.
  - [x] Authentication response bodies.
  - [x] Complete secret-bearing launch command lines.
- [x] Add centralized sensitive-field and URL-query redaction.
- [x] Restrict login WebView navigation to exact approved Epic HTTPS origins.
- [x] Verify WebView message origin independently at receipt time.
- [x] Enforce a small, length-bounded WebView message schema and expected message type.
- [x] Reject malformed, oversized, unexpected, and replayed login messages.
- [x] Enforce HTTPS and an explicit host policy for authenticated API and CDN requests.
- [x] Disable redirects or validate every redirect's scheme, host, and credential handling.
- [x] Introduce a safe storage-key codec for remote app names, versions, and other identifiers.
- [x] Resolve every application-data path under a canonical application root.
- [x] Add seeded-secret and path-adversary regression tests.

### Exit gate

- Canary credentials and signed query values never appear in captured logs.
- Unexpected WebView origins and messages are rejected.
- Authorization cannot leak to an unapproved host or redirect.
- Remote identifiers cannot escape the application-data directory or create colliding unsafe names.

### UI impact

A narrowly scoped `LoginPage` security change only; no visual redesign.

---

## Phase 3 — Establish portable project boundaries

This phase changes project ownership without intentionally changing product behavior.

### Tasks

- [x] Create `Crimson.Core` targeting `net10.0`.
- [x] Move portable domain records, enums, and value types into Core.
- [x] Move manifest, chunk, CDL, rolling-hash, update-planning, and logical manifest-path code into Core.
- [x] Create `Crimson.Infrastructure` targeting `net10.0`.
- [x] Move portable downloader and HTTP repository implementations into Infrastructure.
- [x] Use `Microsoft.Extensions.Logging` abstractions in portable projects.
- [x] Keep Serilog configuration in host projects.
- [x] Replace test source links with project references.
- [x] Retarget portable tests to plain `net10.0`.
- [x] Add Windows and Ubuntu CI jobs for portable builds and tests.
- [x] Preserve existing namespaces temporarily where that reduces unnecessary churn.
- [x] Keep compatibility classes/facades in the existing WinUI application where needed.

### Exit gate

- Core and Infrastructure compile on Windows and Ubuntu.
- Tests reference production assemblies normally.
- No portable project references WinUI, `Windows.*`, or `App.GetService`.
- Existing WinUI build, behavior, and publish remain green.

### UI impact

None.

---

## Phase 4 — Harden networking and Epic binary parsing

Treat all Epic API, manifest, URL, chunk, filename, and metadata input as hostile until bounded and validated.

### Networking tasks

- [x] Add cancellation tokens to repository operations.
- [x] Apply endpoint-specific connect, header, and body timeouts.
- [x] Retry only safe/idempotent operations.
- [x] Honor `Retry-After` where applicable.
- [x] Add maximum response body sizes.
- [x] Stream large responses rather than buffering without bounds.
- [x] Return typed failures instead of raw strings or swallowed exceptions.
- [x] Make payload platform explicit and independent of host OS.

### Parser tasks

- [x] Implement an exact bounded reader with checked arithmetic and section tracking.
- [x] Reject short reads instead of accepting partially filled buffers.
- [x] Bound compressed and decompressed manifest sizes.
- [x] Bound decompression expansion ratio.
- [x] Bound strings, paths, tags, file counts, chunk counts, and cumulative chunk parts.
- [x] Reject negative counts, overflow, overlapping sections, unsupported versions, duplicates, and trailing invalid data.
- [x] Bound chunk decompression to supported protocol limits.
- [x] Verify declared compressed and uncompressed chunk sizes.
- [x] Validate downloaded chunk GUID, rolling hash, and SHA-1 where required by the protocol.
- [x] Validate trusted manifest digests before caching or use.
- [x] Preserve final whole-file hash verification.
- [x] Add malformed-input, property, and fuzz tests.

### Parser test budgets

- Pull requests run the checked-in malformed corpus, every truncated prefix of the synthetic valid samples, and 2,000 fixed-seed random inputs per parser target with generated inputs limited to 512 bytes.
- The deterministic malformed-input test completes within 10 seconds on the supported CI runner. Any per-input timeout, unexpected exception type, path escape, or process crash fails the gate.
- Nightly fuzzing runs each parser target for at least 15 minutes with generated inputs up to 1 MiB, a one-second per-input timeout, and a 256 MiB process-memory-growth ceiling for the malformed corpus.
- Every nightly failure retains the exact input, seed, target, exception or timeout, and build identifier as a reproducible artifact.

### Exit gate

- Bit flips in manifest data, chunk headers, chunk bodies, and assembled files prevent publication.
- Truncated, overflowing, oversized, and compressed-bomb inputs fail in bounded time and memory.
- Fuzzing produces no hangs, crashes, path escapes, or uncontrolled allocations within the budgets defined above.

### UI impact

None.

---


## Phase 5 — Harden versioned atomic JSON state

Keep the existing JSON approach small and explicit. This phase completes its durability and migration rules without introducing a database or generic persistence framework.

### Tasks

- [x] Inventory every mutable state file, its owner, maximum size, current schema, and authoritative fields.
- [x] Give each state category an explicit schema version and typed migration path; do not treat one global envelope version as a semantic migration strategy.
- [x] Serialize writes through one owner per state file and publish immutable/read-only snapshots to consumers.
- [x] Keep every state path under the canonical application root and enforce category-specific response/file size limits.
- [x] Discover raw historical JSON without modifying it, fingerprint the source, and import each category independently.
- [x] Make every migration idempotent and retain the legacy input through at least one subsequently successful application version.
- [x] Reject unknown future schemas without overwriting, resetting, or falling back to an empty installation state.
- [x] Validate primary and backup contents before recovery; quarantine corrupt inputs with actionable diagnostics instead of clearing the whole category.
- [x] Define the operation journal as the recovery authority for install/update operations. Other state files are derived views and are reconciled from the journal after interruption.
- [x] Use monotonic journal revisions and explicit phases; never assume two JSON files were committed atomically.
- [x] Keep operation journals, staging, and backups until both file publication and installed-state reconciliation are durable.
- [x] Reuse `AtomicJsonFile` and `AtomicFile`; add narrower stores only where an actual caller needs category-specific behavior.

### Exit gate

- Repeated migrations produce the same state and never alter the retained legacy source.
- Interrupted writes recover the last valid primary or backup without losing recognized installations.
- Unknown future schemas and corrupt categories fail safely with actionable diagnostics.
- Fault injection between every operation-journal and installed-state write reconciles to one complete authoritative state after restart.

### UI impact

None.

---

## Phase 6 — Establish platform boundaries and filesystem semantics

Do this before the transactional installer rewrite so the new engine is not built on Windows-only paths, credential handling, or process APIs.

### Tasks

- [x] Create `Crimson.Platform.Windows` only as concrete Windows behavior is moved from the WinUI host.
- [x] Move Windows credential protection, application-directory selection, direct process execution, and filesystem capability probes behind the Core contracts used by real callers.
- [x] Keep WinUI as the Windows composition root; this phase changes adapters, not XAML or layout.
- [x] Replace raw manifest-controlled path strings with a `ManifestRelativePath` value type storing logical segments.
- [x] Treat both `\` and `/` as manifest separators on every host OS.
- [x] Reject Windows and POSIX dangerous forms independent of host:
  - [x] POSIX roots.
  - [x] Drive roots and drive-relative paths.
  - [x] UNC and device paths.
  - [x] Empty, `.` and `..` segments.
  - [x] NUL and alternate streams.
  - [x] Trailing dots/spaces.
  - [x] Windows reserved device names.
- [x] Validate the complete manifest for case-insensitive and Unicode-normalization collisions before mutation.
- [x] Materialize one canonical manifest spelling.
- [x] On Linux imports, resolve existing components case-insensitively only when exactly one match exists.
- [x] Reject ambiguous imported trees.
- [x] Add narrow filesystem capability contracts for:
  - [x] Effective write/flush/rename/delete probe.
  - [x] Available and total space.
  - [x] Volume/device identity.
  - [x] Atomic rename support.
  - [x] Symlink, junction, and reparse-point detection for install roots and existing descendants.
- [x] Replace Windows ACL inspection with an effective operation probe.
- [x] Keep staging and backups on the destination filesystem.
- [x] Reject manifest symlink entries instead of materializing them.
- [x] Reject install roots or mutation paths that traverse symlink, junction, or reparse-point components.
- [x] Revalidate containment and link state immediately before each manifest-driven install create, overwrite, move, and delete operation.
- [x] Define and document supported local and network filesystem behavior.

### Supported filesystem policy

- Install, update, repair, import, move, and uninstall are supported only on filesystems classified as local by the platform and only after the effective write/flush/rename/delete probe succeeds.
- Network and unclassified filesystems are rejected. Crimson does not claim durable atomic publication on SMB, NFS, FUSE, cloud-synchronized folders, or other remote/virtual filesystems.
- Staging, backups, journals, and the live installation remain on one volume. Cross-volume moves are rejected.
- Read-only locations, insufficient available space, unsupported long paths, linked install trees, and case-insensitive import ambiguities fail before publication.
- Existing untracked files are preserved. Link-based trees remain unsupported, and the non-elevated same-user race limitation in architectural decision 7 still applies.

### Exit gate

Portable filesystem tests pass on Windows and Linux for mixed separators, traversal, roots, case and Unicode collisions, rejected symlink/reparse roots and descendants, ambiguous imports, read-only roots, insufficient space, long paths, and same/different devices. Windows adapter tests cover credential protection, application directories, process execution, and effective filesystem probes.

### UI impact

None.

---

## Phase 7 — Refactor LibraryManager into a portable library service

Refactor the smaller manager first so it provides stable snapshots and manifest identities to the installer.

### Tasks

- [x] Replace the early cache-only `ILibraryService` prototype with the authoritative portable library service.
- [x] Return immutable `GameSnapshot` values rather than store-owned mutable `Game` instances.
- [x] Replace mutable manager event payloads with immutable, sequence-numbered snapshots.
- [x] Serialize refreshes with a single in-flight refresh task or semaphore.
- [x] Fetch remote metadata concurrently but apply state mutations through one serialized atomic writer before publishing a snapshot.
- [x] Return typed refresh results and preserve the previous valid snapshot on failure.
- [x] Update refresh timestamps only after success.
- [x] Track asset build identity separately from downloaded manifest identity.
- [x] Persist the manifest digest/build actually installed.
- [x] Replace simple version-string inequality with manifest-identity-aware update classification.
- [x] Add regression coverage for Rocket League-style asset/manifest version mismatch.
- [x] Separate launch construction into:
  - [x] `ILaunchPlanner`
  - [x] `IRuntimeProfileResolver`
  - [x] `IGameProcessRunner`
- [x] Represent process arguments as an ordered argument list, not a prequoted string.
- [x] Replace synchronous `WaitForExit` with asynchronous process tracking.
- [x] Block launch while an install transaction is unresolved.
- [x] Keep a temporary `LibraryManager` facade if required by current viewmodels.

### Exit gate

Headless tests cover concurrent refreshes, failed refresh/retry, metadata application, update detection, asset/manifest mismatch, immutable snapshots, exact launch arguments/environment, and launch rejection during recovery.

### UI impact

No layout changes. Existing calls/events may be adapted through a compatibility facade.

---

## Phase 8 — Replace InstallManager internals with a transactional engine

This is the largest phase. Implement and validate each subphase before proceeding.

### 8.1 Single operation coordinator

- [ ] Replace `async void ProcessNext` with one observable queue-processing task.
- [ ] Introduce a serialized command channel for enqueue, pause, resume, cancel, shutdown, and recovery.
- [ ] Make queue and history mutation thread-safe.
- [ ] Ensure one active operation context owns each worker set.
- [ ] Return typed command and terminal results.

### 8.2 Per-operation state

- [ ] Move queues, worker tasks, cancellation, pause state, progress, chunk references, manifest identities, staging paths, journal revision, and timing into `InstallOperationContext`.
- [ ] Keep the service as coordinator rather than storing all current-operation implementation state globally.
- [ ] Do not invoke external events while internal locks are held.

### 8.3 Pure deterministic planning

- [ ] Create pure planners for install, update, repair, import, uninstall, and move.
- [ ] Produce immutable serializable plans.
- [ ] Reconstruct plans from manifest identity and verified progress rather than trusting serialized absolute IO tasks.
- [ ] Validate all destinations and resource requirements before download begins.

### 8.4 Transactional staging and commit

- [ ] Persist the operation and intended plan atomically before mutation.
- [ ] Download and reconstruct under an operation-specific staging directory on the destination filesystem.
- [ ] Verify staged files before modifying the live installation.
- [ ] Journal additions, replacements, removals, backups, and publication progress.
- [ ] Persist `ReadyToCommit` before live-tree changes.
- [ ] Move existing owned targets to operation backup.
- [ ] Publish verified staged files through same-volume atomic renames where supported.
- [ ] Defer removed-file disposal until installation metadata commits.
- [ ] Update installed manifest identity in the authoritative versioned state before cleanup.
- [ ] Mark the operation complete before deleting staging and backups.
- [ ] Prevent launch while commit or recovery is active.

Application state and filesystem changes cannot be committed atomically. The required service invariant is:

> After startup recovery, either the complete old version or complete new version is launchable. A mixed installation is never launchable.

### 8.5 Durable pause and resume

Pause must:

- [ ] Stop dequeuing new work.
- [ ] Signal in-flight workers.
- [ ] Await every worker at a defined safe point.
- [ ] Flush staged files.
- [ ] Persist manifest identity, phase, verified artifacts, progress, and revision atomically.
- [ ] Publish `Paused` only after the checkpoint is durable.

Resume must:

- [ ] Create exactly one worker set.
- [ ] Rebuild a deterministic plan.
- [ ] Revalidate staged artifacts.
- [ ] Resume only required work.
- [ ] Remain cancellable through a correctly initialized operation lifecycle.

### 8.6 Cancellation, shutdown, and recovery

- [ ] Cancellation before commit removes only operation-owned staging data.
- [ ] Cancellation during commit transitions to recovery rather than ad hoc deletion.
- [ ] Shutdown waits for a durable safe checkpoint.
- [ ] Remove fixed sleeps and timing-based state assumptions.
- [ ] Make startup recovery idempotent.
- [ ] Recover forward or roll back from every journal phase.
- [ ] Emit one stable terminal result per operation.

### 8.7 Uninstall and move

- [ ] Uninstall moves manifest-owned files into transaction trash first.
- [ ] Commit `NotInstalled` before permanently deleting transaction trash.
- [ ] Preserve untracked files.
- [ ] Remove only empty manifest-owned directories and operation metadata.
- [ ] Keep rejecting cross-volume move with a typed result until the same-volume transactional lifecycle passes its exit gate.

Cross-volume copy/verify/switch/delete is deferred until after engine and frontend parity unless it becomes a release requirement.

### Fault-injection budgets

- Pull requests inject process termination and one atomic-state-write failure after every journal transition in the synthetic old/new lifecycle; the matrix completes within 10 minutes on the supported CI runner.
- Nightly runs every journal phase against cancellation, access denial, disk exhaustion, staged-file corruption, journal corruption, and process termination with a two-minute timeout per scenario.
- Every failure retains the starting state, operation plan, journal revisions, injected fault, resulting filesystem inventory, and recovery result without secret-bearing URLs.

### Exit gate

A deterministic synthetic lifecycle passes on real filesystems:

```text
install
restart
launch-plan
pause
shutdown
resume
repair
failed update
rollback/recovery
successful update
cancel
uninstall
```

Fault injection after every journal transition must recover to a complete old or new manifest. Cancellation, access denial, disk exhaustion, corruption, and process termination must not delete untracked files or expose a mixed installation as launchable.

### UI impact

Keep a compatibility facade with the current manager-facing commands/events until shared viewmodels are migrated.

---

## Phase 9 — Finish framework-neutral MVVM without redesigning the UI

CommunityToolkit.Mvvm remains the shared MVVM library. Reconcile the parked Presentation prototype with proven WinUI behavior; do not treat prototype code as the specification.

### Tasks

- [ ] Complete and reconcile `Crimson.Presentation` targeting `net10.0`; remove prototype-only workflows that do not match the engine contracts.
- [ ] Migrate one workflow at a time: characterize current WinUI behavior, implement the shared viewmodel, switch WinUI bindings/composition to it, pass parity tests, then remove the duplicate WinUI viewmodel.
- [ ] Keep WinUI XAML, layout, styling, and user-visible workflow unchanged except for security or lifecycle correctness.
- [ ] Constructor-inject all viewmodel dependencies.
- [ ] Remove `App.GetService` from viewmodels and shared services.
- [ ] Remove direct `new Storage()` and other infrastructure construction from viewmodels.
- [ ] Move `LibraryItem`, `DownloadManagerItem`, and selectable DLC presentation state out of view code-behind/domain folders.
- [ ] Replace `BitmapImage` properties with validated `Uri?` or nullable source strings.
- [ ] Replace WinUI glyph strings with semantic action/icon enums.
- [ ] Introduce narrow presentation contracts only when used:
  - [ ] `IUiDispatcher`
  - [ ] `IFolderPickerService`
  - [ ] `IInstallDialogService`
  - [ ] `INavigationService`
  - [ ] `IExternalPathLauncher`
- [ ] Make UI dispatch awaitable.
- [ ] Move async loading out of constructors and into explicit activation/load methods.
- [ ] Add deterministic activation/deactivation or disposal.
- [ ] Ensure every long-lived manager subscription has a matching unsubscribe.
- [ ] Replace untyped navigation payloads with typed routes.
- [ ] Add or reshape:
  - [ ] `ShellViewModel`
  - [ ] `LoginViewModel`
  - [ ] `LibraryViewModel`
  - [ ] `GameInfoViewModel`
  - [ ] `DownloadsViewModel`
  - [ ] One shared `CurrentOperationViewModel`
  - [ ] `AppInstallDialogViewModel`
  - [ ] `SettingsViewModel`
- [ ] Keep platform code in the host for WebView events, native window lifecycle, tray ownership, pickers, native dialogs, and platform image conversion.
- [ ] Add headless viewmodel tests.

### Anti-abstraction rules

Do not create universal abstractions for controls, windows, brushes, images, tray icons, WebViews, or arbitrary dialogs. Share state and intent, not widget APIs. Do not add clipboard or other platform contracts until a real feature requires them.

### Exit gate

- Presentation builds under plain `net10.0`.
- Every shared viewmodel is constructible with test doubles.
- Shared viewmodels contain no WinUI, Avalonia, `Windows.*`, `Crimson.Views`, or native platform types.
- Activation/deactivation tests prove subscriptions do not leak.
- Existing WinUI layout and workflows remain substantially unchanged.
- WinUI consumes the shared viewmodels for every migrated workflow; no second behavioral implementation remains in WinUI.
- Each workflow has parity tests covering the shared viewmodel through the WinUI adapters before Avalonia consumes it.

### UI impact

Internal binding, adapter, and code-behind changes only. Avoid visual redesign.

---

## Phase 10 — Add Linux platform adapters and a headless Proton host

Prove that the engine works on Linux before building Avalonia views.

### Tasks

- [ ] Create `Crimson.Platform.Linux` and a small CLI/headless host.
- [ ] Implement XDG config, data, cache, state, and log directories.
- [ ] Implement Secret Service/libsecret credential storage.
- [ ] Provide a clear session-only mode when secure credential storage is unavailable.
- [ ] Define the supported headless authentication bootstrap before implementation. Use a system-browser callback only if Epic's current flow is verified to support it; otherwise accept a short-lived exchange code through protected standard input, never through command-line arguments, environment variables, or logs.
- [ ] Let deterministic headless tests inject fake authentication; live headless tests use a pre-provisioned Secret Service session and verify refresh separately from initial credential acquisition.
- [ ] Implement Linux filesystem capability and durability behavior.
- [ ] Discover Steam Proton, Proton-GE, `compatibilitytools.d`, and configured custom runtimes.
- [ ] Represent Proton as an explicit runtime profile containing command, version, prefix/compatibility data, and environment.
- [ ] Define a per-game prefix policy.
- [ ] Construct structured `LaunchSpec` values with executable, working directory, ordered arguments, environment, runtime, and prefix.
- [ ] Resolve executable case safely on Linux filesystems.
- [ ] Explicitly request Epic Windows assets under Linux.
- [ ] Return actionable errors for missing Proton, missing prefixes, inaccessible filesystems, and unsupported mounts.
- [ ] Test with a redistributable synthetic Windows executable before a commercial game.
- [ ] Define and publish an initial Proton/filesystem support matrix.

### Exit gate

On Linux, the headless host can securely load and refresh a pre-provisioned session, load the library, install the synthetic Windows fixture, restart and recover, repair, update, uninstall, and launch through Proton without secret-bearing logs. Any claimed interactive headless bootstrap must have its own live validation.

### UI impact

None.

---

## Phase 11 — Add the Avalonia host

The existing Avalonia prototype remains parked until the shared engine, Linux host, and Presentation contracts pass their exit gates. Reuse code only where it matches those proven contracts.

### Tasks

- [ ] Rebuild and complete `Crimson.Avalonia` targeting `net10.0`; do not count the parked shell or placeholder pages as workflow completion.
- [ ] Reference unchanged Core, Infrastructure, and Presentation projects.
- [ ] Implement Avalonia adapters for dispatch, folder picking, install dialogs, navigation, image conversion, external paths, and login hosting.
- [ ] Implement shell/login, library, game details, downloads, and settings views.
- [ ] Compose Windows and Linux platform adapters appropriately.
- [ ] Keep WinUI available until Avalonia reaches feature and migration parity.
- [ ] Decide later whether Avalonia replaces the Windows frontend or remains an alternative host.

### Exit gate

- Shared viewmodel tests pass without framework-specific conditional compilation.
- Avalonia completes the supported Windows/Linux workflows.
- WinUI remains buildable until an explicit retirement decision.

---

## Phase 12 — Production hardening and release engineering

### Tasks

- [ ] Resolve nullable warnings in security, parser, persistence, installer, and library paths first.
- [ ] Enable analyzers and warnings-as-errors per portable project after establishing a clean baseline.
- [ ] Add locked NuGet restore.
- [ ] Add dependency vulnerability and license policy.
- [ ] Add CodeQL/static security analysis.
- [ ] Add deterministic builds, SBOM, artifact checksums, and provenance.
- [ ] Pin CI actions to reviewed commit SHAs.
- [ ] Separate PR, nightly, release-candidate, and release workflows.
- [ ] Run extended parser fuzzing and install-recovery fault matrices nightly.
- [ ] Add operation and transaction correlation IDs.
- [ ] Bound and redact logs and support bundles.
- [ ] Sign Windows artifacts and verify signatures after packaging.
- [ ] Add equivalent Linux package/signing metadata.
- [ ] Test clean-machine install, upgrade, interrupted upgrade, rollback, and uninstall.
- [ ] Document schema compatibility, downgrade limitations, recovery procedures, supported OS versions, filesystems, and Proton versions.

### Exit gate

A stable release is blocked unless:

1. No known critical/high security or data-loss finding remains.
2. Required Windows and Linux matrices pass from a clean checkout.
3. Parser fuzzing and transaction fault injection meet the budgets defined in Phases 4 and 8.
4. No critical module relies solely on skipped or developer-local fixtures.
5. Upgrade and interrupted-upgrade recovery pass from the previous supported release.
6. Logs and support bundles pass seeded-secret scanning.
7. Signatures, hashes, SBOM, and provenance verify independently.

---

# Core service contracts

Exact signatures may evolve, but these ownership boundaries should remain.

## Installer

```text
IInstallService
  Snapshot
  EnqueueAsync
  PauseAsync
  ResumeAsync
  CancelAsync
  GetSizesAsync
  Changed event with immutable sequence-numbered snapshots
```

Contract requirements:

- One active operation per app.
- One coordinator owner per queue.
- `PauseAsync` returns only after a durable checkpoint.
- `CancelAsync` returns only after terminal or durable recovery state.
- Events are never invoked while internal locks are held.

## Library

```text
ILibraryService
  GetLibraryAsync
  GetGameAsync
  RefreshAsync
  LaunchAsync
  Changed event with immutable snapshots
```

Contract requirements:

- Failed refresh preserves the last valid snapshot and returns a typed error.
- Update detection uses installed manifest identity, not only display build strings.
- Launch refuses while recovery is unresolved.

## Platform/runtime

```text
IAppDirectoryProvider
ICredentialStore
IInstallFileSystem
IRuntimeProfileResolver
ILaunchPlanner
IGameProcessRunner
```

## Presentation

```text
IUiDispatcher
IFolderPickerService
IInstallDialogService
INavigationService
IExternalPathLauncher
```

Do not add interfaces preemptively. Add an adapter only when the core or presentation layer otherwise depends on a concrete platform behavior.

---

# Versioned state and filesystem transaction model

Versioned atomic state files cannot atomically include filesystem changes. Install operations therefore use a write-ahead recovery protocol with durable, operation-specific journals.

The operation journal is the sole recovery authority for an in-progress install/update. Installed-game state, queue/history views, and library snapshots are derived records. An atomic write protects one JSON file only; no code may assume that two state files changed atomically. Every journal transition carries a monotonic revision and explicit phase, is flushed before the corresponding filesystem mutation, and is safe to replay.

## Recommended commit sequence

1. Persist the operation identity, complete intended plan, phase `Planned`, and revision to its atomic journal.
2. Create destination-local staging and backup roots.
3. Download and assemble staged files.
4. Verify every staged file.
5. Persist `ReadyToCommit` before changing the live tree.
6. Move existing owned files to backup, persisting each completed transition with a new journal revision.
7. Publish staged files, persisting each completed transition with a new journal revision.
8. Verify the resulting owned tree where required.
9. Persist `FilesPublished`.
10. Update the installed manifest identity and local installation record through its single state-file owner.
11. Persist `MetadataCommitted`, then `Completed`.
12. Remove staging, backup, and transaction trash only after the completed journal is durable.

Startup must inspect and reconcile incomplete journals before exposing games as launchable or publishing derived snapshots. Recovery is idempotent and either completes publication or restores the old version. If a derived state file disagrees with an incomplete journal, the journal wins until recovery reaches a terminal phase.

## Content-addressed manifest cache

Use a trusted digest as the filename rather than app name or version:

```text
cache/manifests/<digest-prefix>/<full-digest>.manifest
```

A versioned atomic manifest index maps the digest to app ID, asset build version, manifest build version, payload platform, size, and validation timestamp.

---

# Test strategy

## Unit/property tests

- Parser bounds and exact reads.
- Paths, case, Unicode, and storage keys.
- Manifest/update/install planners.
- State-machine transitions and idempotency.
- Update identity classification.
- URI/host policy and header isolation.
- Credential record/version behavior with fake stores.
- Viewmodel state and command behavior.

## Component tests

- Scripted HTTP failures, redirects, timeouts, truncation, and cancellation.
- Fault-injecting filesystem: disk full, short writes, access denial, rename failure, and crash points.
- Versioned state migration, concurrent access, corruption, backup, and recovery behavior.
- Journal/derived-state disagreement, revision replay, unknown future schemas, and idempotent category migration.
- Full synthetic download, parse, stage, verify, commit, and rollback.
- Pause/resume/restart and queue behavior.
- Log capture with seeded secrets.

## Integration tests

- Headless lifecycle on Windows and Linux.
- Real symlink/reparse and case-sensitive filesystem behavior.
- Upgrade from every supported historical schema.
- Windows process launch fixture.
- Proton process launch fixture.
- Optional live Epic API contract canary using dedicated credentials; never required for deterministic PR tests.

## End-to-end smoke tests

- WinUI login-origin rejection and normal login flow.
- Library, install, pause/resume, update, repair, launch, and uninstall.
- Avalonia/Proton flow after those hosts exist.
- Routine live Windows lifecycle validation uses Among Us for install, restart, verify, launch, repair, and uninstall because it is small enough for frequent execution.
- Every release candidate must pass a full Windows lifecycle with Rocket League against the current Epic Live manifests. Rocket League is the high-user-base update and scale canary; deterministic and routine validation must not depend on it.
- Fortnite may be used as an additional high-scale canary, but it does not replace the mandatory Rocket League release-candidate lifecycle.
- Live runs use isolated install roots and dedicated test credentials where available. Record app ID, asset build, manifest build/digest, platform, terminal states, and artifact hashes; never retain tokens or signed URLs.

Coverage percentages are supporting metrics, not substitutes for failure-path tests. Critical parser, authentication, persistence, and transaction branches should receive substantially higher coverage than passive DTOs or platform view code.

---

# Near-production completion criteria

Crimson is considered near production-grade when:

- No known critical/high security or data-loss findings remain.
- Updates are recoverably transactional.
- Pause/resume survives process termination without duplicate workers or writes.
- All remote data is bounded and integrity-checked before publication.
- Credentials and signed URLs cannot appear in logs.
- Mutable application state has typed schema migrations, atomic per-file writes, and journal-recoverable multi-file transitions.
- Portable core and presentation tests pass on Windows and Linux.
- Core and presentation contain no UI-framework or OS implementation dependencies.
- WinUI and Avalonia are thin hosts over the same tested services and viewmodels.
- A real Windows lifecycle and a real Linux/Proton lifecycle pass.
- Release artifacts are reproducible, signed, and independently verifiable.

## Next execution slice

The next implementation slice after this plan revision is:

1. Checkpoint the current coherent source and test state without including local plans, graph output, or tool artifacts.
2. Re-run a fresh interactive WinUI login to close the corrected authentication/WebView exit gate; make no visual changes.
3. Run the Phase 4 deterministic malformed-input budgets on Windows and Ubuntu CI, retaining any failure input.
4. Audit and check off the completed Phase 0–4 task lists against recorded evidence.
5. Implement Phase 5 beginning with the mutable-state inventory, typed category schemas, legacy-format fixtures, and unknown-future-version tests.
6. Keep the Presentation and Avalonia prototypes buildable but parked. Do not add WinUI visual improvements or Avalonia features in this slice.
7. Do not begin Phase 6 until the durable JSON state exit gate passes.
