# In-app notifications

`NotificationHost` is a reusable WinUI overlay backed by the singleton
`NotificationService`. It appears in the main window's bottom-right corner,
uses an accessible InfoBar with polite announcements, and does not move focus,
activate another window, or block navigation.

Messages dismiss after six seconds. Hovering or focusing the notification pauses
dismissal; leaving it starts a fresh six-second reading period. The close button
dismisses immediately. A newer notification replaces the current one and starts
its own timer. Old callbacks and interactions cannot dismiss the replacement.
There is no persistent history or backlog.

To reuse it from another feature, marshal onto the UI dispatcher and use the
registered service:

```csharp
notifications.Show(new AppNotification(
    "Download complete",
    "Your game is ready to play.",
    NotificationSeverity.Success));
```

An optional duration overrides the default. Information, success, warning, and
error severities are supported. Another host can bind to the same service with
`Bind`; the host handles subscriptions through its Loaded/Unloaded lifecycle.
The service and its timer are platform-neutral and can be tested with a
`TimeProvider` without waiting for real time.

## Newly owned games

`LibraryManager.GamesAdded` reports additions after publishing the updated
library, once the metadata needed to display them is available. The shell shows
one game's title, or a single count message for multiple games from one refresh.
DLC, initial synchronization, metadata-only updates, and initial synchronization
for a different account are silent. Cached snapshots establish the baseline after
restart. Failed metadata fetches retain pending additions for the next successful
refresh in the same session. Repeated refreshes do not repeat notifications.
Logout dismisses the current message, and queued additions from an earlier
account are ignored.

The core suite covers ownership detection, delayed metadata, batches, duplicate
refreshes, account boundaries, timer expiry, manual dismissal, paused dismissal,
and replacement races. CI runs it on Linux and Windows and compiles the WinUI
host in both unpackaged and MSIX builds.

For a Windows UI smoke check, acquire a game in Store and return to Library.
Confirm the card is added and a success message appears without interrupting
navigation. Confirm automatic dismissal, pause on hover/focus, the close button,
and grouped messages for multiple additions. Also confirm that initial login,
ordinary page navigation, and signing into another account do not show messages
for existing games.
