# Changelog

Release notes are taken from the matching entry here, so every release needs one.

## [Unreleased]

- Sync cloud saves before launch and after exit for games that declare cloud save support.
- Ask which copy survives when local and cloud saves diverge, with options to keep local, keep cloud, or skip.
- Lock Play while a game runs unless it allows multiple instances, and show cloud save progress on the game page.
- Track a game by its whole process tree, so sessions that hand off to another executable still upload saves when the game really exits.
- Size content dialogs per dialog instead of forcing one width globally.
- Drop the unused Windows AI/ML runtime payload from release assets.

## [1.0.0] - 2026-10-23

Initial Release
