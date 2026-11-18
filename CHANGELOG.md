# Changelog

Release notes are taken from the matching entry here, so every release needs one.

## [Unreleased]

- Sync cloud saves before launch and after exit for games that declare cloud save support.
- Ask which copy survives when local and cloud saves diverge, with options to keep local, keep cloud, or skip.
- Lock Play while a game runs unless it allows multiple instances, and show cloud save progress on the game page.
- Track a game by its whole process tree, so sessions that hand off to another executable still upload saves when the game really exits.
- Size content dialogs per dialog instead of forcing one width globally.
- Drop the unused Windows AI/ML runtime payload from release assets.
- Ubisoft Connect games install, update, verify, repair, import, move and uninstall from Crimson, with no Ubisoft Connect installation needed. Downloads appear on the Downloads page with speed and progress, and can be paused, resumed and cancelled.
- Install languages: choose them in the install dialog and change them later in the game's properties. Crimson downloads only the files the chosen languages need.
- Import from Ubisoft Connect: games already installed by the official client, or in any folder you pick, are added without downloading them again.
- Ubisoft cloud saves sync before a game starts and after it exits. When both copies changed, you choose which to keep, and a local save is backed up before it is replaced.
- Ubisoft games built on the newer R2 runtime, such as Trackmania, start from Crimson.
- The Ubisoft library loads from Ubisoft's ownership service in about a second. Game names and artwork follow the app language.
- The registry entries and prerequisites a Ubisoft game needs are applied after one administrator prompt per operation. The prompt covers registry changes; prerequisite installers run without elevation and may show their own Windows prompts. If you never approve the registry prompt, the game still installs, and the prompt is offered again at the next launch or repair. A Ubisoft game does not start until its setup prompt is approved, because without that setup it fails to start.
- Ubisoft manifests are checked against their published ID before use, and resumed downloads verify completed files and partial slices instead of trusting the progress file.
- The Ubisoft setup helper exchanges its request over a private pipe tied to the Crimson process, and never executes game-supplied programs as administrator.
- Crimson points Ubisoft games at its runtime when you start a game, not when you install it, because Ubisoft Connect changes that setting whenever it runs. The administrator prompt appears only if the setting changed since the last launch. Uninstalling the last Ubisoft game removes the setting, unless Ubisoft Connect owns it.
- Before an administrator prompt, Crimson shows what the prompt is for and opens the prompt from there, after a 3-second countdown or right away with **Continue**. If you decline the Windows prompt, Crimson stays on that screen and offers **Try again**; **Cancel** skips it.

## [1.0.0] - 2026-10-23

Initial Release
