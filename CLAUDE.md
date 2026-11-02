# ailocalpack

Open-source, cross-platform (Windows, macOS, Linux) desktop app that lets a player **install** a community localization for any game they own, or **generate** a new one with an AI agent and **publish** it for others.

Status: solution skeleton, app shell (navigation, chrome, theme), settings store and Settings → General in place; Core lists installed Steam games and keeps them in a persisted game catalog (`ailocalpack games list`); the toolkit detects game engines and anti-cheats (`ailocalpack toolkit detect`); no UI library yet. Treat this file as the product spec and keep it current: record each settled decision in its section, and remove the matching item from _Open questions_.

## Constraints

- Every platform is first-class: Windows, macOS, Linux (including Steam Deck). Store discovery, file paths, and agent installation all differ per OS.
- No server of our own: the app, GitHub, and the player's AI agent account are the whole system.
- AI usage goes through the player's own agent subscription or API key; the project pays for no inference.
- Open source.

UI screens and their states: [`design/README.md`](design/README.md).

Implementation tasks (numbered; the user names one to implement): [`tasks/README.md`](tasks/README.md). Follow its *Definition of done* and tick the task's checkbox when finished.

## Domain terms

- **Game**: an installed title, found through a store or added manually.
- **Game identity**: the stable ID that matches a game across machines (store ID or file fingerprint).
- **Game version**: the specific build (store build ID or file-hash set); packs can break between versions.
- **Pack**: one localization of one game into one language. Made of a string table + recipe, or a binary patch as fallback. Has revisions and a status (`alpha` / `verified`).
- **Recipe**: the part of a pack naming which toolkit tool rebuilds game files from the strings.
- **Toolkit**: the app's built-in, engine-specific extract/rebuild code; also exposed to agents over MCP.
- **Agent**: an external AI coding agent (Claude Code, Codex) that the app drives to generate a pack.
- **Registry**: the separate public GitHub repo of packs.

## Roadmap

- **MVP**: text only. Steam + manually added folders. All three OSes. Install from the registry, generate with an agent (Claude Code and Codex), publish.
- **Phase 2**: audio (TTS). **Phase 3**: textures (localized images). The UI shows both as "coming soon" from day one.
- Post-MVP: more stores (Epic, GOG, EA app), "own API key / local model" agent adapter, manual string editor, cross-store game matching.

## Core flow

1. **Discover**: scan store libraries (Steam in MVP; Steam `appmanifest_*.acf` gives app ID and build ID) and let the player add a game folder manually.
2. **Identify**: compute game identity and version (see _Game identity_).
3. **Browse**: list packs for the player's language (default: OS language), with search across other languages; sort by status and rating.
4. **Install**: one button. Deterministic, agent-free (see _Install_).
5. **Generate**: when no suitable pack exists, offer to generate one with an agent (see _Generation_).
6. **Publish**: offer to share the result through the registry (see _Publishing_).

## Stack

- **C# / .NET + Avalonia UI.** Chosen over MAUI (no official Linux) and Tauri/Electron for real Linux support and direct use of .NET modding libraries (AssetsTools.NET, CUE4Parse, UAssetAPI, BepInEx ecosystem).
- **Distribution: Velopack + GitHub Releases** with delta updates. Linux: AppImage first, Flatpak/Flathub later. Signing: macOS notarization from the start; Windows via free open-source signing (SignPath Foundation).
- **Desktop app structure**: MVVM with CommunityToolkit.Mvvm, `Microsoft.Extensions.DependencyInjection` (graph in `Composition`), view-model-first navigation (`INavigationService` back stack with Library as root; page views mapped by `DataTemplate`s in `App.axaml`, no reflection view locator). Back: chrome button, Esc (the page gets it first via `PageViewModel.TryHandleEscape`, e.g. to clear a search), Alt+←, mouse back button. Theme: Fluent + own tokens (`Styles/Theme.axaml`, colors from `design/drafts`); setting `System / Dark / Light`, default System (follows the OS, live).
- **Settings screen**: sections are DI singletons (`SettingsSection`, registered in `Composition` in list order; one without rows shows "coming in a later version"), each with groups of rows (`DropdownSettingRow`, `SegmentedSettingRow`, `ToggleSettingRow`) that edit `AppSettings` through `SettingsStore` and carry search keywords (`*_Keywords` resources). Search (`SettingsSearch`) is a case- and diacritics-insensitive substring match on label, keywords, group and section titles, in the UI language and in English. Deep link: `pages.Create<SettingsViewModel>(SettingsSectionId.X)`. A failed save logs and shows the stored value again.
- **Game language** ("Translate games into"): `AppSettings.GameLanguage`, a `GameLanguages` tag or `null` = OS language (`GameLanguages.Resolve`: setting → OS culture, where other regions map `es-*` → `es-419` and `pt-*` → `pt-PT` → English).
- **Steam discovery** (`Core/Steam`): `VdfParser` reads text KeyValues (`.vdf`/`.acf`) into `VdfObject` (ordered, case-insensitive lookup, first of duplicate keys wins; conditionals like `[$WIN32]` skipped; unknown escapes keep the backslash; errors are `VdfParseException` with line/column). `SteamLocator.FindRoots()` returns folders containing `steamapps/`, in priority order, never throws: Windows `HKCU\Software\Valve\Steam\SteamPath` then `%ProgramFiles(x86)%\Steam`; macOS `~/Library/Application Support/Steam`; Linux `$XDG_DATA_HOME/Steam` (default `~/.local/share/Steam`), `~/.steam/{steam,root,debian-installation}`, Flatpak `~/.var/app/com.valvesoftware.Steam/.local/share/Steam`, Snap `~/snap/steam/common/.local/share/Steam`; candidates that are the same folder through symlinks (any path component) are reported once. Inputs come from `OsEnvironment` (home, variables) and `IWindowsRegistry`, so tests use a temp home and a fake registry. `AILOCALPACK_STEAM_ROOT=<dir>` replaces discovery (dev, tests).
- **Steam library scan** (`SteamLibraryScanner`, best effort, never throws for disk state): libraries = each root itself + `steamapps/libraryfolders.vdf` entries (current `"0" { "path" }` and pre-2021 `"1" "D:\\..."` formats), deduplicated by symlink-free path; a listed folder without `steamapps/` is reported `IsAvailable = false` (unplugged drive, SD card), not an error. A game = `appmanifest_*.acf` with `StateFlags` bit 4 (fully installed; kept while an update is pending), a plain `installdir` (no separators, not `..`), path `<library>/steamapps/common/<installdir>`; first copy of an app ID wins; sorted by name. Tools are skipped (`SteamTools`: known app IDs of Steamworks Redistributables, Proton, Steam Linux Runtime, plus a strict name pattern for future versions). Last played = latest of `userdata/*/config/localconfig.vdf` (`UserLocalConfigStore/Software/Valve/Steam/apps/<id>/LastPlayed`, across all users) and the manifest `LastPlayed`. Broken files are logged and skipped.
- **Game catalog** (`Core/Games`): `GameCatalog` keeps every `Game` (ID, name, `GameSource` Steam/Manual, install and library path, `GameVersion`, engine, anti-cheat, last played, `IsAvailable`) in `<data>/games.json` (`schemaVersion`, DTOs apart from domain types; atomic writes). `GameId` text is canonical only (`steam:<appid>` decimal without leading zeros, `fp:<64 lowercase hex>` SHA-256), so parse/format round-trips. `GameVersion` is a build ID or a file-hash set (relative `/` paths → SHA-256 hex), compared by content. `Refresh(SteamScan)` uses the pure `GameCatalogMerge`: scanned games added/refreshed (engine, anti-cheat kept; a lost build ID keeps the known version), a different build ID → `GameVersionChanged`; a stored Steam game missing from the scan stays as unavailable if its library is listed but not available or Steam was not found at all, otherwise it is removed; manual games stay, available = folder exists. `AddManual` / `RemoveManual` (manual games only) / `Update` (ID and source fixed); `Changed` carries the new list and `GameAdded` / `GameRemoved` / `GameVersionChanged` (no event when nothing changed). Best effort: corrupted file → `games.json.<utc>.bak` + empty; unreadable → empty and never saved this session; broken entries skipped; failed save logged, kept in memory.
- **Engine detection** (`Toolkit/Engines`, `EngineDetector`, best effort, never throws for disk state): marker rules per engine run on the game folder, its direct subfolders and the macOS `.app` bundles in both (a folder holding a bare bundle `Contents/Info.plist`, as some Steam Mac installs do, counts as a bundle). Unity: `<Name>_Data/` with `globalgamemanagers` / `mainData` / `data.unity3d` / `Managed/` / `il2cpp_data/` next to `<Name>.exe|.x86_64` and `UnityPlayer.dll|.so` (macOS `Contents/Resources/Data`, `Contents/Frameworks/UnityPlayer.dylib`); IL2CPP = `GameAssembly.*` or `il2cpp_data/`, Mono = `Managed/`. Unreal: `<Project>/Content/Paks/*.pak|*.utoc` (+ `Engine/` for high confidence); version from the macOS bundle folder (`Contents/UE4` vs `Contents/UE`) or the build branch compiled into the shipping binary (`+UE5+Release-`, UTF-16 or ASCII; a custom branch gives no version). RPG Maker: `js/rmmz_core.js` (MZ), `www/js/rpg_core.js` (MV), macOS in `Contents/Resources/app.nw`. Ren'Py: `renpy/` + `game/` (macOS `Contents/Resources/autorun`). Godot: `.pck` starting with `GDPC` (Wwise banks share the extension) or a pack embedded in the executable (file ends with u64 size + `GDPC`); major version from the pack header. Result (`EngineDetection`): `EngineMatch` = `EngineKind` (engine + variant), confidence (`High` full layout / `Medium` partial / `Low` single hint), marker and main executable (relative, `/`-separated; Windows → Linux → macOS bundle so the choice is the same on every machine; crash handlers, installers and redistributables skipped). Ranking: confidence, then depth; two different engines tied on both = ambiguous (unknown, candidates listed). Lookups ignore case. `EngineKind` text is the engine ID stored in `Game.Engine`: `unity:mono|il2cpp`, `unreal:4|5`, `rpgmaker:mv|mz`, `renpy`, `godot:<major>`, `unknown` (variant optional; parse/format round-trip). `EngineDetector.UpdateCatalog` detects available games without a stored engine or added / version-changed in the last refresh.
- **Anti-cheat** (`AntiCheatPolicy` in `Core/AntiCheat`, `AntiCheatDetector` in `Toolkit/AntiCheat`): the bundled policy is `Core/AntiCheat/anti-cheat.json` (embedded resource), in the same format the registry will ship as `policy/anti-cheat.json` (task 65): `{schemaVersion: 1, antiCheats: [{id, name, markers}], games: [{game, antiCheat}], allowed: [{game, note}]}`. IDs are lowercase `[a-z0-9-]` (`none` reserved); a marker is a file name, a folder with trailing `/` or a short path (`pb/pbcl.dll`), wildcards `*`/`?` only in the last part, case-insensitive. Bundled: Easy Anti-Cheat, BattlEye, Riot Vanguard, nProtect GameGuard, XIGNCODE3, PunkBuster, HoYoverse mhyprot. `Parse` rejects invalid files (`AntiCheatPolicyException`). The detector walks the game folder breadth-first (depth ≤ 6, ≤ 20 000 folders, macOS bundles included, never throws) and returns every anti-cheat found with its marker (relative, `/`-separated, folders end with `/`), shallowest first. `Game.AntiCheat` stores the file finding only: anti-cheat ID, `none`, or `null` = not checked (`AntiCheatDetector.UpdateCatalog`: same trigger as engines). Blocking is decided at use time by `AntiCheatPolicy.Resolve(game)`: stored ID, else the `games` list (anti-cheats that leave no files); the `allowed` whitelist (officially mod-friendly games) lifts the block; an unknown ID shows as its name.
- **CLI** (`AiLocalPack.Cli`, `System.CommandLine` 2.x): `CliApp.Build(OsEnvironment)` holds the command tree; no command prints help (exit 0). `games list [--json]` scans Steam, refreshes the catalog, detects engines and anti-cheats (`UpdateCatalog`) and prints it: a table (engine display name, anti-cheat name or `(allowed)`, local time; unavailable games marked) or JSON `{ libraries: [{path, available}], games: [{id, source: "steam"|"manual", appId (null for manual), name, buildId, engine (engine ID or null), antiCheat (ID, "none" or null), blocked, installPath, libraryPath, lastPlayed (UTC ISO 8601 or null), available}] }`. `toolkit detect <folder> [--json]` prints engine, confidence, marker, executable (ambiguous: the candidates) and `anti-cheat: <name> (<marker>)` / `none`, or JSON `{engine, name, confidence, marker, executable, ambiguous, candidates: [{engine, name, confidence, marker, executable}], antiCheats: [{id, name, marker}]}`; a missing folder exits 1. CLI output is English only (not UI resources). Tests run the built CLI as a child process (`CliRunner`, extra env vars; a throwaway `AILOCALPACK_HOME` unless one is given) against a `SteamFixture` tree (TestKit).
- **Logging**: Serilog rolling file in `<data>/logs` via `Microsoft.Extensions.Logging` (daily, 7 files, 10 MB cap); unhandled exceptions (AppDomain, UI dispatcher, unobserved tasks) are logged.
- **App folders** (`AppPaths` in Core, resolved from an injected `OsEnvironment`): three roots, `config` (settings), `data` (`games.json` = game catalog, `logs/`, `backups/`, `work/` = generation working copies), `cache`. Windows: `%APPDATA%\ailocalpack` / `%LOCALAPPDATA%\ailocalpack` / `%LOCALAPPDATA%\ailocalpack\cache`. macOS: `~/Library/Application Support/ailocalpack` (config and data) / `~/Library/Caches/ailocalpack`. Linux: XDG config / data / cache homes + `ailocalpack` (relative XDG values ignored). `AILOCALPACK_HOME=<dir>` puts all three under `<dir>/{config,data,cache}` (dev, tests, portable).
- **Settings**: `AppSettings` (immutable record, `System.Text.Json` source generation, camelCase) in `<config>/settings.json`, owned by the `SettingsStore` singleton (`Load` at startup, `Update(s => s with {...})`, `Changed`). Writes are atomic (`AtomicFile`: temp file + flush + rename). `schemaVersion` + `SettingsMigrator` steps (n → n+1); adding a property with a default needs no migration. `AppSettings` is a positional record and every setting is a constructor parameter with its default: the STJ source generator sets an init-only property missing from the file to `default` (a missing `true` flag would read `false`). `SettingsApplier` (started after `Load`) applies UI language and theme at startup and on every change, so settings apply immediately. A corrupted file is moved to `settings.json.<utc yyyyMMdd-HHmmss>.bak` and defaults are written; an unreadable one gives defaults in memory and is left alone; a file from a newer app is read best-effort. Settings never stop the app from starting.
- **App UI is multilingual from the start**: strings in resource files, top ~10 player languages, initially AI-translated; community fixes via PRs. Language: setting (`AppSettings.UiLanguage`, Settings → General) → OS UI culture → English; switchable at runtime.
  - **Format: `.resx`** in `src/AiLocalPack.Desktop/Resources`: `Strings.resx` is English (neutral, `NeutralLanguage=en`), `Strings.<lang>.resx` per other language (satellite assemblies). The build generates the internal typed `Strings` class (MSBuild `StronglyTyped*` metadata, no designer file in git). Supported languages are listed in `UiLanguages.Supported`.
  - XAML: `{l:Loc Key}` (`xmlns:l="using:AiLocalPack.Desktop.Localization"`), updates live on a language switch. Code: `Localizer` (DI singleton = `Localizer.Current`) with keys as `nameof(Strings.Key)`: indexer, `Format`, `Plural`; it raises `CultureChanged` so view models re-raise their text properties.
  - Plurals: keys `Base_One` / `_Few` / `_Many` / `_Other` with exactly the CLDR forms of the language (`PluralRules`: en one/other, ru one/few/many); `{0}` is the count with digit grouping. Numbers and dates are formatted with the UI culture (a specific one like `ru-RU` is kept for its region).
  - `ResourceFilesTests` guard every locale: same keys as English, same `{n}` placeholders, complete plural forms, no unlisted language shipped.

## Repositories

Both live in the GitHub organization **`ailocalpack`** (not a personal account: the index URL is baked into the app and must never move).

- **`ailocalpack/ailocalpack`** (this repo): one .NET solution: Avalonia UI, core, toolkit, MCP server, agent adapters, and a CLI used by registry CI to validate packs.
  - Layout: `AiLocalPack.slnx`; `src/AiLocalPack.{Core,Toolkit,Agents,Mcp,Cli,Desktop}`; `tests/AiLocalPack.*.Tests` (xunit.v3 on Microsoft.Testing.Platform).
  - Tests: one `tests/AiLocalPack.<Project>.Tests` per `src` project, plus `tests/AiLocalPack.TestKit` (shared helpers: `TempDir`, `FakeHttpHandler`, `FakeClock`, `TestLogger<T>`, `SteamFixture`, `EngineFixture` (per-engine synthetic layouts for Windows / Linux / macOS); referenced by every test project via `tests/Directory.Build.props`). Run with `dotnet test` from the root.
  - Test style: assertions only through **AwesomeAssertions** (`x.Should()...`, global using in `tests/Directory.Build.props`), never xunit `Assert.*`. Every test follows **AAA** with explicit `// Arrange`, `// Act`, `// Assert` comments; the Act step is one action, and an expected exception is captured as `var act = () => ...;` then asserted with `act.Should().Throw<...>()`. One behaviour per test.
  - UI language in tests: an assembly fixture runs `Localizer.Current` in English, so tests assert English text whatever the machine language. A test that switches `Localizer.Current` goes in `[Collection(typeof(UiLanguageSwitchCollection))]` (runs alone) and restores English in `finally`.
  - Time: code that reads the clock takes a `TimeProvider` (default `TimeProvider.System`); tests pass `FakeClock`.
  - Desktop tests build the service graph with `TestServices.Build()` (a fresh temp app home per graph, deleted with the provider, never the player's real folders; an optional `configure` replaces services, e.g. a fake `IAppTheming` or a `TestLogger`).
  - UI tests: Avalonia headless via `HeadlessUnitTestSession` and plain `[Fact]` (`HeadlessUi.RunAsync` in Desktop.Tests). `Avalonia.Headless.XUnit` 12.1.x is built against xunit.v3 3.x and breaks discovery on xunit.v3 4.x; switch to `[AvaloniaFact]` once it supports v4.
  - .NET 10 (`global.json`). Shared settings in `Directory.Build.props`: `TreatWarningsAsErrors` and NuGet audit (`all`, level `low`) fail the build on any vulnerable package, transitive included. Versions only in `Directory.Packages.props` (central package management; pin patched transitive versions there).
- **`ailocalpack/registry`**: packs, JSON schemas, GitHub Actions, GitHub Pages index at `https://ailocalpack.github.io/registry/index.json`.

## Game identity

- Game ID = store ID (`steam:1091500`). Manually added games get a fingerprint from hashes of the main executable and key data files (`fp:<SHA-256, 64 lowercase hex>`).
- Version = store build ID, else a file-hash set.
- A pack records hashes of the files it touches; before install the app reports compatible / probably compatible / incompatible.

## Install

- **Deterministic and agent-free**: only the app's own built-in code runs. Agent-written code never runs on other players' machines.
- Reversible: back up every overwritten file; support uninstall.
- **Game updates**: on app start and in the background (tray), re-check hashes and build IDs of games with installed packs. If a store update or file verification wiped the pack and the version is still compatible, re-apply automatically (with a fresh backup). If incompatible, notify and offer a newer pack revision or generating the missing strings.
- **Anti-cheat**: games with anti-cheat (EAC, BattlEye, Vanguard, ...), detected by known files and a registry list, are blocked for both install and generation, with an explanation. A registry flag can whitelist games that officially allow mods.
- **Fonts**: the toolkit checks game fonts for the target script's glyphs; if missing, it swaps in open (OFL) fonts from a whitelist bundled with the app. Packs reference fonts, never ship them.

## Generation

- **Agent adapters behind one interface.** Drive the agent CLI headless (`claude -p`, `codex exec`) with streaming JSON progress, using the player's own subscription login. Both Claude Code and Codex in MVP. An "own API key / local model" adapter comes later.
- **Agent installation**: use one already on `PATH`; otherwise download the official standalone binary into an app-managed folder (no Node.js, no admin rights), verifying signature/hash. Sign-in runs the agent's own login flow; the app never handles agent tokens.
- **Sandbox**: the agent works only on a copy of the game files, never the game folder.
- **Toolkit first**: the toolkit is exposed to the agent as an MCP server. MVP engines: Unity, Unreal Engine, RPG Maker MV/MZ, Ren'Py, Godot. The agent writes its own code only for unknown formats; proven scripts get promoted into the toolkit in later releases.
- **Pipeline** (stages, persisted to disk, resumable when interrupted or the subscription limit runs out; show a size estimate before starting):
  1. Explore the game and extract strings via the toolkit.
  2. Build a glossary (names, terms, tone).
  3. The app chunks strings and feeds them to the agent with the glossary.
  4. Rebuild and do a test run.
- The agent gets string length limits where they can be determined and must preserve placeholders and formatting tags; a validator checks both.

## Pack format

- **Primary: strings + recipe.** A string table (key → translation, JSON/PO) plus a recipe naming the toolkit tool that rebuilds game files locally from the player's own copy.
- **Fallback: binary patch** (xdelta) produced on the author's machine, for engines the toolkit cannot rebuild.
- **Data only.** Executable components (mod loaders like BepInEx, XUnity.AutoTranslator) come only from a maintainer-controlled whitelist in the registry: the manifest names the dependency and version, and the app downloads it from the official release and checks its hash. Registry CI rejects any executable file in a pack.
- **No original text, no game files.** Packs hold keys, translations, and recipes. The editor (later) shows original text from the player's local game copy.

## Registry

- **Layout**: `games/<game-id>/<language>/<pack-id>/` (`<language>` is a BCP 47 tag from `GameLanguages.Tags` in Core: the languages Steam supports, e.g. `ru`, `pt-BR`, `es-419`, `zh-Hans`) with `manifest.json` and `strings/` (later `audio/`). Several competing packs per game + language are allowed. Game versions are a supported-builds/hashes list in the manifest, not folders; updating a pack for a new game version is a new revision of the same pack.
- **Storage**: manifests and string tables live in git (reviewable in PRs). Binary parts (patches, later audio) are uploaded by CI to GitHub Release assets and referenced from the manifest by URL + hash. Packs are stored compressed.
- **Client reads**: never clones and never calls the GitHub API for browsing. It fetches a static `index.json` from GitHub Pages and only the files it installs.
- **Lifecycle**: a pack PR auto-merges into `main` once CI passes (schema, hashes, no executables) with `status: alpha` (installable, shown with a warning). It becomes `verified` after N independent players (not the author; e.g. N = 3) confirm "works"; a GitHub Action flips the status. Maintainers can grant or revoke `verified` manually.
- **Rating**: one GitHub Discussion per pack. 👍 reaction = like; templated comments = works / doesn't work reports and reviews. Voting requires GitHub sign-in. A scheduled Action aggregates ratings and confirmations into `index.json`.
- **Legal**: disclaimer, a clear DMCA takedown process, and a per-game `opt-out` flag for studios that object. Packs for pirated copies are not encouraged.
- **Licenses**: app under **Apache-2.0**; registry content under **CC BY-SA 4.0**.

## Publishing

- GitHub sign-in via **OAuth Device Flow** (no client secret in the app); token stored in the OS keychain.
- The app forks the registry via the API, commits the pack to a branch, and opens a PR.
- Before publishing, it compares with existing packs and offers a new revision of an existing pack (own or someone else's) instead of a duplicate.

## Open questions

- **Audio (phase 2)**: TTS engine (local open models like Piper/XTTS vs. cloud via the player's API key, or both via adapters); voice cloning of original actors (default: no, stock voices); audio codec and bitrate for compression.
- **Archive format** for binary pack parts.
- **N for `verified`** and anti-abuse rules for confirmations (account age, one vote per account).
- **Manifest schema**: exact fields (author, agent used, revision, status, supported builds, file hashes, dependencies, fonts).
