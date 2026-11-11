# Foundations: folders, settings, logging, UI localization

App folders, settings storage, logging, multilingual UI resources. Part of the product spec in `CLAUDE.md`: keep it current; record settled decisions in the matching section here.

## App folders

(`AppPaths` in Core, resolved from an injected `OsEnvironment`): three roots, `config` (settings), `data` (`games.json` = game catalog, `installed-packs.json` = installed packs, `install-journal.json` = transaction in progress, `install.lock`, `covers/` = manual game covers, `logs/`, `backups/`, `work/` = generation working copies, `local-packs/` = packs generated on this machine (registry layout + build reports), `agents/` = agents the app installed, `github-account.json` = the signed-in GitHub account (the token is in the OS keychain, `docs/publishing.md`), `pack-ratings.json` = the player's marks on installed packs: played, Later, liked, reported (task 63)), `cache` (`covers/`, `file-hashes.json`, `registry/` = saved index, `packs/` = downloaded packs, `fonts/` = downloaded whitelisted fonts, `dependencies/` = downloaded release archives of whitelisted dependencies). Windows: `%APPDATA%\ailocalpack` / `%LOCALAPPDATA%\ailocalpack` / `%LOCALAPPDATA%\ailocalpack\cache`. macOS: `~/Library/Application Support/ailocalpack` (config and data) / `~/Library/Caches/ailocalpack`. Linux: XDG config / data / cache homes + `ailocalpack` (relative XDG values ignored). `AILOCALPACK_HOME=<dir>` puts all three under `<dir>/{config,data,cache}` (dev, tests, portable).

## Settings

`AppSettings` (immutable record, `System.Text.Json` source generation, camelCase; derived properties are `[JsonIgnore]`) in `<config>/settings.json`, owned by the `SettingsStore` singleton (`Load` at startup, `Update(s => s with {...})`, `Changed`). Writes are atomic (`AtomicFile`: temp file + flush + rename). `schemaVersion` + `SettingsMigrator` steps (n → n+1); adding a property with a default needs no migration. `AppSettings` is a positional record and every setting is a constructor parameter with its default: the STJ source generator sets an init-only property missing from the file to `default` (a missing `true` flag would read `false`). `SettingsApplier` (started after `Load`) applies UI language and theme at startup and on every change, so settings apply immediately. A corrupted file is moved to `settings.json.<utc yyyyMMdd-HHmmss>.bak` and defaults are written; an unreadable one gives defaults in memory and is left alone; a file from a newer app is read best-effort. Settings never stop the app from starting.

## Logging

Serilog rolling file in `<data>/logs` via `Microsoft.Extensions.Logging` (daily, 7 files, 10 MB cap); unhandled exceptions (AppDomain, UI dispatcher, unobserved tasks) are logged.

## App UI is multilingual from the start

strings in resource files, top ~10 player languages, initially AI-translated; community fixes via PRs. Language: setting (`AppSettings.UiLanguage`, Settings → General) → OS UI culture → English; switchable at runtime.
- **Format: `.resx`** in `src/AiLocalPack.Desktop/Resources`: `Strings.resx` is English (neutral, `NeutralLanguage=en`), `Strings.<lang>.resx` per other language (satellite assemblies). The build generates the internal typed `Strings` class (MSBuild `StronglyTyped*` metadata, no designer file in git). Supported languages are listed in `UiLanguages.Supported`.
- XAML: `{l:Loc Key}` (`xmlns:l="using:AiLocalPack.Desktop.Localization"`), updates live on a language switch. Code: `Localizer` (DI singleton = `Localizer.Current`) with keys as `nameof(Strings.Key)`: indexer, `Format`, `Plural`; it raises `CultureChanged` so view models re-raise their text properties.
- Plurals: keys `Base_One` / `_Few` / `_Many` / `_Other` with exactly the CLDR forms of the language (`PluralRules`: en one/other, ru one/few/many); `{0}` is the count with digit grouping. Numbers and dates are formatted with the UI culture (a specific one like `ru-RU` is kept for its region).
- `ResourceFilesTests` guard every locale: same keys as English, same `{n}` placeholders, complete plural forms, no unlisted language shipped.
