# Reviewing and translating UI text

## English source

Review `Crimson.Core/Resources/UiStrings.resx`. This is the neutral English catalog used by both the portable Core and WinUI. Each entry has a stable key, its displayed value, and a comment naming the source screens or classes that use it.

The initial extraction preserves existing wording, capitalization, punctuation, and spelling. Similar entries such as `Loading…`, `Loading...`, and `Loading....` remain separate for review. No copy has been intentionally removed or rewritten.

Edit the `<value>` of an entry to change its wording everywhere that key is used. Some keys are shared across screens. To remove an unwanted message entirely, change its presentation call site as well: an empty value is not a substitute for deciding when the UI should display text.

Composite strings use numbered placeholders, for example:

```xml
<data name="ImportRepairFilesFormat" xml:space="preserve">
  <value>Verified {0} files: {1} require repair</value>
</data>
```

Preserve the placeholder numbers and format specifiers. Placeholders can be reordered, but do not insert game titles, account data, file paths, or other runtime values into the catalog. Escape XML characters such as `&amp;` and `&lt;`.

`UiStrings.cs` provides strongly typed accessors. Changing only values does not require regenerating it. After adding, removing, or renaming keys, run:

```powershell
./scripts/update-ui-strings.ps1
```

## Runtime behavior

XAML text binds to static `UiStrings` properties. C# presentation code uses the same properties and `UiStrings.Format` for dynamic text. Resource lookup uses `CurrentUICulture`; numeric formatting keeps the system's `CurrentCulture`.

Settings includes a language picker, defaulting to **System Language**. An explicit choice is saved as a culture code; System Language is saved without an override. At startup, before creating the main window, Crimson selects the saved language or the nearest supported parent of the system UI language. Unsupported system languages fall back to English. After a saved change, a confirmation dialog offers **Restart Crimson** or **Later**. Restart uses the Windows App SDK lifecycle API to terminate and reopen the app, rather than closing the window into the tray. Choosing Later leaves a restart button in Settings. Active or queued operations block restart, and failures are shown inline. Returning to the startup choice clears the restart note and button.

Included translations are German (`de`), Spanish (`es`), French (`fr`), Italian (`it`), Japanese (`ja`), Brazilian Portuguese (`pt-BR`), and Simplified Chinese (`zh-CN`), alongside English. The language names use native spelling so they remain recognizable. New English-only entries fall back to English until translated.

Translations use sibling files such as `UiStrings.fr.resx`, preserving the English keys and format placeholders. .NET builds satellite resources automatically. Use valid .NET culture codes: Japanese is `ja`, not `jp`. When adding another language, also add its culture code and native display name to `AppLanguage`.

## Boundaries

The catalog includes app-owned labels, buttons, tooltips, accessibility descriptions, status text, and displayed failures and suggestions. It also includes presentation formats for sizes, rates, dates, playtime, and shortcut descriptions.

It excludes logs, internal stoppage reasons, parser/validation exception messages not displayed directly, generated GitHub issue titles/bodies and diagnostic fields, protocol and JSON field names, filesystem identifiers, packaging identity and branding metadata, and remote Epic game titles/descriptions/media. Those are not translatable app copy.

Trace C# strings to their presentation consumer before extracting them. A `StatusMessage` assignment alone is not proof of UI use: structured operation failures override internal stoppage and verification messages. DLC validation exceptions are likewise replaced by a user-facing error; the separate `BlockedMessage` shown in Properties remains localized.

Layout, operation guards, control availability, and the text of persisted historical operations are unchanged by extraction. Historical messages already saved in app data are not retroactively translated.

## Validation

Resource tests verify that every English key has a typed accessor, a usage comment, and valid format placeholders. Presentation tests verify that local XAML text uses resource or data bindings, including item templates. Existing behavior assertions still check the rendered English values through resource resolution. Review the actual wording in the catalog before release.
