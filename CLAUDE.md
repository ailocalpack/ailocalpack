# AI Local Pack (`ailocalpack`)

Open-source, cross-platform (Windows, macOS, Linux) desktop app that lets a player **install** a community localization for any game they own, or **generate** a new one with an AI agent and **publish** it for others.

Treat this file and `docs/*.md` as the product spec and keep them current: record each settled decision in the matching section (here for principles and conventions, in `docs/` for component details), and remove the matching item from _Open questions_.

## Status

Done through task 67: app shell, settings, Steam + manual game library with engine / anti-cheat detection, registry index (fetched, cached, offline), pack compatibility check, download cache, install transaction (backup, rollback, uninstall, re-apply, crash recovery, whitelisted dependencies, binary patches), re-check after game updates, Activity panel, tray + OS notifications, recipe tools for RPG Maker MV/MZ, Ren'Py, Godot 3/4, Unity, Unreal 4/5 with font swap, string validator, agent adapters (Claude Code, Codex, fake) with install and sign-in, toolkit MCP server, working copies + estimate, persisted generation jobs, pipeline stages Setup → Extract → Glossary → Translate by chunks → Build (rebuild, font swap, test run, local pack) running for real, Generation tab start form starting real jobs from the app, generation progress card with pause / resume / cancel, state banners, auto-resume after the limit and Activity rows, glossary review (edits saved at once, form of address, Regenerate keeping the player's edits, Activity decision), generation result (tiles, Install & play of the local pack through the install service, Discard, New translation, tried-in-game detection, share strip), Packs → Mine and update mode (`Translate new strings`: the installed pack's next revision with only the new and changed strings), unknown engines (the agent's own extractor / rebuilder in a sandboxed workspace, a binary patch pack from our own VCDIFF encoder), GitHub sign-in (Device Flow, token in the OS keychain, `github login / whoami / logout`), Settings → GitHub (account, inline Device Flow panel reused by publishing), publish client (fork, commit with the Git Data API, PR or a fix pushed to the open one, new pack vs new revision with credits, patch archives through the fork; `pack publish`), publish flow UI (one-click `Publish` from the result strip and Packs → Mine once tried in game, first-time dialog with Device Flow + CC BY-SA 4.0 consent, similar-pack dialog with the recommended new revision, strip states sending / CI running / failed / published with the 0/3 meter, Activity running item and outcome), PR tracking (open PRs read on start and on the clock with ETags and backoff; CI failed with the reason from the check annotations as a strip state, Mine row, Activity decision and Settings → GitHub row with `Fix and resubmit` to the same PR; merged / closed recorded, merged / closed / verified feed events; Settings → GitHub *My published packs*), ratings (the installed pack's *Your rating* in Packs details once played: 👍 on its Discussion, Works / Doesn't work reports with comment and build, one per revision; the game page's rating prompt after the first session; sign-in on first use; `pack rate`), registry ratings and `verified` (`ratings collect`: Discussions opened for new packs, likes and confirmations counted with the anti-abuse rules; `index build` flips a pack to `verified` at 3 confirmations, `policy/verified.json` overrides; hourly *Publish index*), registry policy lists (opt-out: packs left out and refused, the app blocks; the registry's anti-cheat rules / listed games / whitelist published in the index and merged over the bundled list, games checked again when the rules change; schemas for every policy file; `LEGAL.md` with takedown, opt-out and wrong-block processes and issue templates), Settings → Storage (sizes with a stacked bar, backups moved to another folder as a transaction, working copies deleted with their job, cache cleared; `storage show / move-backups / clear-cache`), app updates and Settings → About (Velopack over GitHub Releases: checked at start and every 6 h, downloaded in the background, `Restart to update` / Activity `Restart now`; version, channel, platform, links, licenses, third-party notices generated at build time from the shipped packages). Feature-by-feature list with CLI commands: `docs/status.md`.

## Docs

Component details live in `docs/`. Read the matching doc before changing or extending a component, and update it with the change. Sections are cited by name as _Name_ (a `## Name` heading; find it with `grep -n '^## Name' docs/*.md`).

- `docs/games.md`: Steam discovery and library scan, game catalog, game language, launch and sessions, engine detection, anti-cheat, folder recognizer, manual fingerprint / version, file hash cache, game icons, game identity details.
- `docs/toolkit.md`: recipe tools (`IRecipeTool`, engines switched off per OS, `rpgmaker`, `renpy`, `godot`, `unity`, `unreal`), fonts (whitelist, reading, swap), string validator.
- `docs/packs-and-registry.md`: manifest / string table details, registry index and client, pack compatibility, pack download and cache, binary patches (VCDIFF, LZMA2), dependencies whitelist, pack validation, index build, registry CI.
- `docs/install.md`: install transaction (journal, backup, rollback, uninstall, re-apply, recovery, dependencies), backup location (moving the backups), game update re-check.
- `docs/generation.md`: agent adapters and their contract, Claude Code and Codex adapters, agent install and sign-in, toolkit MCP server, working copies and estimate, generation jobs, pipeline and prompts, translate stage, build stage (test run, local packs).
- `docs/desktop-app.md`: app structure (MVVM, DI, navigation), controls and colours (radius scale, button kinds, focus ring, contrast), settings screen and sections (About: app updates, third-party notices), library, game page, packs tab, generation tab, dialogs, add game folder, covers, startup, install flow (UI), Activity panel, app icon, tray and background.
- `docs/foundations.md`: app folders, settings storage, logging, UI localization (`.resx`, `Localizer`, plurals).
- `docs/publishing.md`: GitHub sign-in (Device Flow, OAuth App, scope, session), keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service), publish client (fork, commit, PR, revisions and credits, patch archives, errors), publish flow, PR tracking, ratings (Discussion likes and reports, the report comment format).
- `docs/cli.md`: CLI framework and `games` / `toolkit` commands (area commands are also in their area's doc).

UI screens and their states: `design/README.md` (drafts in `design/drafts`).

Implementation tasks (numbered; the user names one to implement): `tasks/README.md`. Follow its *Definition of done* and tick the task's checkbox when finished. `design/` and `tasks/` are the maintainer's local working notes, kept out of the repository (`.git/info/exclude`); references like "task 21" or "design §2" in code point into them.

## Commands

```sh
dotnet build                                   # whole solution; warnings are errors
dotnet test                                    # all tests (xunit.v3 on Microsoft.Testing.Platform)
dotnet run --project src/AiLocalPack.Desktop   # the desktop app
dotnet run --project src/AiLocalPack.Cli -- <command>   # e.g. games list, toolkit detect <folder>, gen run <game>
dotnet publish src/AiLocalPack.Cli -c Release -r <rid>  # self-contained single-file CLI
```

Dev environment variables:

- `AILOCALPACK_HOME=<dir>`: config, data and cache under `<dir>/{config,data,cache}` (dev, tests, portable); never touches autostart.
- `AILOCALPACK_STEAM_ROOT=<dir>`: replaces Steam discovery.
- `AILOCALPACK_INDEX_URL=<url | folder>`: registry index location, e.g. `tests/fixtures/registry`.
- `AILOCALPACK_TOOLKIT_ALL_ENGINES=1`: no engine switched off on this OS (RPG Maker on macOS).
- `AILOCALPACK_UPDATE_SNAPSHOTS=1`: rewrite prompt snapshots in Pipeline tests.
- `AILOCALPACK_SECRETS_FILE=<path>`: secrets (the GitHub token) in a plain-text JSON file instead of the OS keychain (dev, tests only). With `AILOCALPACK_HOME` set the keychain service is `ailocalpack (<home>)`, apart from the real sign-in.
- `AILOCALPACK_REGISTRY=<owner/repo>`: publish to another registry repository (e.g. `ailocalpack/registry-test`) from the app and `pack publish`.
- `AILOCALPACK_UPDATE_URL=<folder | url>`: app updates from a local `vpk pack` output folder (or another http(s) feed) instead of GitHub Releases.
- `AILOCALPACK_GITHUB_CLIENT_ID=<id>`: another OAuth App (Device Flow on); `AILOCALPACK_GITHUB_URL=<url>`: a fake GitHub (`<url>/login/...`, API under `<url>/api/`).

Synthetic test games: `ailocalpack dev fixture rpgmaker|renpy|godot|unity|unreal|custom <dir>` (`custom` = a made-up format no toolkit tool reads). Install by hand: `toolkit rebuild` → `dev apply-files` / `dev uninstall`. Fake agent: `agent run --fake <recording.jsonl>`, `gen run <game> --fake-agent <recording>`.

## Constraints

- Every platform is first-class: Windows, macOS, Linux (including Steam Deck). Store discovery, file paths, and agent installation all differ per OS.
- No server of our own: the app, GitHub, and the player's AI agent account are the whole system.
- AI usage goes through the player's own agent subscription or API key; the project pays for no inference.
- Open source.

## Naming

- **AI Local Pack** is the name players read: window titles, the header logo, tray, OS notifications, autostart entries, UI text in every language, README, social previews (`AppInfo.DisplayName` in Core; resx strings write it out).
- **`ailocalpack`** stays the technical name: the CLI command, the GitHub organization and repositories, app folders (`AppPaths.AppFolderName`), keychain service, MCP server, notification / autostart IDs (`io.github.ailocalpack`), file suffixes and names the toolkit writes into games, agent prompts, User-Agent. Renaming any of them breaks existing installs or packs.

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
- **Distribution: Velopack + GitHub Releases** with delta updates. The app checks the releases of `ailocalpack/ailocalpack` at start and every 6 h, downloads in the background and asks for `Restart to update` (never while a pack installs); Releases are tagged `v<version>` (the About page's *What's new* links there) and the release workflow packs with `vpk` of the same version as the `Velopack` package (decided, task 67; _Settings → About_ in `docs/desktop-app.md`). Third-party notices are generated at build time from the shipped NuGet packages (`build/ThirdPartyNotices.cs`, license texts in `build/licenses`); a new dependency whose license has no text there fails the build. The CLI ships separately as one self-contained single-file binary per OS (the csproj turns on `SelfContained` + `PublishSingleFile` when a RID is given), so registry CI needs no .NET; until releases exist (task 71) the registry CI builds it from source. Linux: AppImage first, Flatpak/Flathub later. Signing: macOS notarization from the start; Windows via free open-source signing (SignPath Foundation).
- **Projects**: `Core` (domain, catalog, registry, packs, install, generation jobs; no UI), `Toolkit` (engine detection, recipe tools, fonts, validator, folder scan), `Agents` (adapters, process runner, agent install), `Mcp` (toolkit MCP server), `Pipeline` (generation stages = Core + Toolkit + Agents + Mcp), `Cli`, `Desktop` (Avalonia app).
- **Desktop app**: MVVM with CommunityToolkit.Mvvm, `Microsoft.Extensions.DependencyInjection` (graph in `Composition`), view-model-first navigation (`INavigationService`, Library as root; page views mapped by `DataTemplate`s in `App.axaml`, no reflection view locator). VM updates from background events go through `IUiDispatcher`; Avalonia objects are created lazily on the UI thread. One modal dialog at a time through `IDialogService`; destructive actions confirm with `ConfirmDialogViewModel`. Details: `docs/desktop-app.md`.
- **Controls and colours**: one family of controls on the palette's tokens: corner radius scale 6 / 8 / 10 / 12 / 14–18 by control size, button kinds `accent` / `ghost` / `bare` / `link` / `danger`, one rounded keyboard focus ring outside every control (replaces Fluent's square one), WCAG contrast (text ≥ 4.5:1, icons and focus ≥ 3:1) in both themes guarded by `ThemeContrastTests`; no colours or radii inline in views (_Controls and colours_ in `docs/desktop-app.md`).
- **Brand icons**: a product the UI names (store, AI agent, game engine, GitHub…) shows its real mark, not a placeholder (letters, a generic gamepad or box), unless the brand's guidelines forbid it. Take marks from Simple Icons (CC0) at a pinned version into `Desktop/Styles/BrandIcons.axaml`; a brand that is missing or forbids it (OpenAI → Codex) gets a neutral Lucide icon. Details: _Brand marks_ in `docs/desktop-app.md`.
- **Settings**: `AppSettings` is a positional record and every setting is a constructor parameter with its default (the STJ source generator sets a missing init-only property to `default`, so a missing `true` flag would read `false`). Changes go through `SettingsStore.Update(s => s with {...})` and apply immediately; settings never stop the app from starting. Details: `docs/foundations.md`.
- **App UI is multilingual**: no hard-coded user-visible text. Strings live in `src/AiLocalPack.Desktop/Resources/Strings.resx` (English) + `Strings.<lang>.resx`; XAML `{l:Loc Key}`, code `Localizer` with `nameof(Strings.Key)`; plurals as `Base_One` / `_Few` / `_Many` / `_Other` with exactly the CLDR forms of the language. Every new key goes into every locale (`ResourceFilesTests` check keys, placeholders and plural forms). CLI output is English only. Details: `docs/foundations.md`.
- **App folders**: `AppPaths` (Core) from an injected `OsEnvironment`: `config`, `data`, `cache` roots per OS; never build these paths by hand. Details: `docs/foundations.md`.
- **Robustness conventions** (used throughout): disk / network state never throws out of scanners, readers and services ("best effort, never throws for disk state"; problems become typed results or are logged and skipped); persisted JSON has `schemaVersion`, DTOs apart from domain types, atomic writes (`AtomicFile`: temp + flush + rename), a corrupted file is moved to `<file>.<utc>.bak` and a newer-format one is never overwritten; downloads are size + SHA-256 checked before they enter a cache (`VerifiedDownloader`); IDs and enum texts have canonical forms that round-trip.
- **Time**: code that reads the clock takes a `TimeProvider` (default `TimeProvider.System`); tests pass `FakeClock`.
- **Logging**: Serilog rolling file in `<data>/logs` via `Microsoft.Extensions.Logging`.

## Repositories

Both live in the GitHub organization **`ailocalpack`** (not a personal account: the index URL is baked into the app and must never move).

- **`ailocalpack/ailocalpack`** (this repo): one .NET solution: Avalonia UI, core, toolkit, MCP server, agent adapters, and a CLI used by registry CI to validate packs.
  - Layout: `AiLocalPack.slnx`; `src/AiLocalPack.{Core,Toolkit,Agents,Mcp,Pipeline,Cli,Desktop}`; `tests/AiLocalPack.*.Tests` (xunit.v3 on Microsoft.Testing.Platform); `schemas/` (JSON Schemas of pack files, shared with the registry); `tests/fixtures/` (sample packs, sample registry, synthetic sample games, agent transcripts); `docs/` (component specs).
  - Tests: one `tests/AiLocalPack.<Project>.Tests` per `src` project, plus `tests/AiLocalPack.TestKit` (shared helpers: `TempDir`, `FakeHttpHandler` (sync or async routes, records method, URL, body and headers), `FakeClock`, `TestLogger<T>`, `SteamFixture`, `EngineFixture` (per-engine synthetic layouts for Windows / Linux / macOS), `RecordingProgress<T>` (synchronous `IProgress<T>` that records reports); referenced by every test project via `tests/Directory.Build.props`) and `tests/AiLocalPack.FakeAgent` (the fake agent program, not a test project). Run with `dotnet test` from the root.
  - Test style: assertions only through **AwesomeAssertions** (`x.Should()...`, global using in `tests/Directory.Build.props`), never xunit `Assert.*`. Every test follows **AAA** with explicit `// Arrange`, `// Act`, `// Assert` comments; the Act step is one action, and an expected exception is captured as `var act = () => ...;` then asserted with `act.Should().Throw<...>()`. One behaviour per test.
  - UI language in tests: an assembly fixture runs `Localizer.Current` in English, so tests assert English text whatever the machine language. A test that switches `Localizer.Current` goes in `[Collection(typeof(UiLanguageSwitchCollection))]` (runs alone) and restores English in `finally`.
  - Desktop tests build the service graph with `TestServices.Build()` (a fresh temp app home per graph, deleted with the provider, never the player's real folders; an optional `configure` replaces services, e.g. a fake `IAppTheming` or a `TestLogger`; fake Steam, no covers, immediate dispatcher, `FakeShell`, `FakeAgentHost` so no test starts a real agent).
  - CLI tests run the built CLI as a child process (`CliRunner`, a throwaway `AILOCALPACK_HOME` unless one is given).
  - UI tests: Avalonia headless via `HeadlessUnitTestSession` and plain `[Fact]` (`HeadlessUi.RunAsync` in Desktop.Tests). `Avalonia.Headless.XUnit` 12.1.x is built against xunit.v3 3.x and breaks discovery on xunit.v3 4.x; switch to `[AvaloniaFact]` once it supports v4.
  - .NET 10 (`global.json`). Shared settings in `Directory.Build.props`: `TreatWarningsAsErrors` and NuGet audit (`all`, level `low`) fail the build on any vulnerable package, transitive included. Versions only in `Directory.Packages.props` (central package management; pin patched transitive versions there).
  - Golden files: building `tests/fixtures/registry` with `index build --generated 2026-11-04T12:00:00Z` (+ `tests/fixtures/registry-ratings.json`) reproduces `tests/fixtures/registry/index.json` byte for byte.
- **`ailocalpack/registry`**: packs, JSON schemas, GitHub Actions, GitHub Pages index at `https://ailocalpack.github.io/registry/index.json`.

## Game identity

- Game ID = store ID (`steam:1091500`). Manually added games get a fingerprint from the hash of the main executable plus engine identity data (Unity `app.info`, RPG Maker title), not content files (`fp:<SHA-256, 64 lowercase hex>`; see _Manual game fingerprint and version_ in `docs/games.md`).
- Version = store build ID, else a file-hash set.
- A pack records hashes of the files it touches; before install the app reports compatible / probably compatible / incompatible (_Pack compatibility_ in `docs/packs-and-registry.md`).

## Install

- **Deterministic and agent-free**: only the app's own built-in code runs. Agent-written code never runs on other players' machines.
- Reversible: back up every overwritten file; support uninstall. One transaction at a time across app and CLI; saving the installed-pack record is the commit point; anything before it rolls back (`docs/install.md`). The player can move the backups to another folder (Settings → Storage, decided in task 66): records name backup folders relative to the backups root, so a move is one more transaction under the same lock, journalled in `<data>/backups-location.json` (_Backup location_ in `docs/install.md`).
- **Game updates**: on app start and in the background (tray), re-check hashes and build IDs of games with installed packs. If a store update or file verification wiped the pack and the version is still compatible, re-apply automatically (with a fresh backup). If incompatible, notify and offer a newer pack revision or generating the missing strings.
- **Anti-cheat**: games with anti-cheat (EAC, BattlEye, Vanguard, ...), detected by known files and a registry list, are blocked for both install and generation, with an explanation. A registry flag can whitelist games that officially allow mods. The registry's `policy/anti-cheat.json` (rules, games by ID, whitelist) travels in `index.json` and is merged over the bundled list (registry rule replaces the bundled one with its ID; whitelists add up and beat detection); a change of detection rules makes the app check every game again (decided, task 65; _Anti-cheat_ in `docs/games.md`). Every install and generation service calls `GameBlocks.EnsureAllowed` first, whatever the UI showed.
- **Fonts**: the toolkit checks game fonts for the letters the translation adds; if missing, it swaps in open (OFL) fonts from a whitelist in the app (small ones bundled, CJK downloaded on demand with a hash check). Packs reference fonts, never ship them (_Fonts_ in `docs/toolkit.md`).

## Generation

- **Agent adapters behind one interface.** Drive the agent CLI headless (`claude -p`, `codex exec`) with streaming JSON progress, using the player's own subscription login. Both Claude Code and Codex in MVP. An "own API key / local model" adapter comes later.
- **Agent installation**: use one already on `PATH`; otherwise download the official standalone binary into an app-managed folder (no Node.js, no admin rights), verifying signature/hash. Sign-in runs the agent's own login flow; the app never handles agent tokens.
- **Sandbox**: the agent works only on a copy of the game files, never the game folder, with only the toolkit MCP server (no built-in file, edit or shell tools); for an unknown engine it also gets file and shell tools, confined by the agent's own sandbox to its workspace in the job folder (see _Unknown engines_ below).
- **Toolkit first**: the toolkit is exposed to the agent as an MCP server (the desktop app's hidden `--mcp-server` mode). MVP engines: Unity, Unreal Engine, RPG Maker MV/MZ (Windows and Linux; on macOS handled like an unknown engine), Ren'Py, Godot. The agent writes its own code only for unknown formats; proven scripts get promoted into the toolkit in later releases.
- **Pipeline** (stages, persisted to disk, resumable when interrupted or the subscription limit runs out; show a size estimate before starting):
  1. Explore the game and extract strings via the toolkit.
  2. Build a glossary (names, terms, tone).
  3. The app chunks strings and feeds them to the agent with the glossary.
  4. Rebuild and do a test run (the translated working copy for 15 s, compared with the untranslated one when it closes early), then save a local pack (_Build stage_ in `docs/generation.md`).
- The agent gets string length limits where they can be determined and must preserve placeholders and formatting tags; a validator checks both.
- **Prompts** are versioned files `src/AiLocalPack.Pipeline/Prompts/<name>.v<N>.md`; a change that alters what the agent does is a new version file. Rendered prompts are snapshot-tested.
- **Start form** (decided, task 52; task 57): `Start generation` needs a ready agent, a finished estimate and enough disk; an unknown engine (or one without a tool on this OS) starts with a warning that the agent writes its own reader and the pack is a binary patch; only a toolkit tool that fails to read the game blocks.
- **Unknown engines** (decided, task 57): the agent writes its extract / rebuild scripts in its own workspace (`agent/` of the job folder, its working folder) and runs them there: Claude Code's Bash in its sandbox (`--settings` with `sandbox.enabled`, `failIfUnavailable`, no unsandboxed commands, no network), Codex's `workspace-write` sandbox; the temporary folder inside the workspace; the game copy only read. The app reads the extractor's `strings.json` and, in Build, takes the files the agent's rebuild wrote into `out/` (existing game files only) and makes a **binary patch pack** with its own VCDIFF encoder; the agent's code stays in the job folder (for later promotion into the toolkit) and never goes into a pack. Details: _Unknown engines_ in `docs/generation.md`.
- **Update mode** (decided, task 56): `Translate new strings` makes the installed pack's next revision, translating only the strings a game update added or changed (string tables keep a hash of each original; the base's translations and glossary are reused). Offered when the installed recipe pack is incompatible **or** was installed / re-applied over files it wasn't made for (probably compatible). Generation always reads the game without the installed pack (originals from the install's backup).
- Details: `docs/generation.md`.

## Pack format

- **Primary: strings + recipe.** A string table (key → translation, JSON) plus a recipe naming the toolkit tool that rebuilds game files locally from the player's own copy.
- **Fallback: binary patch** (VCDIFF, as xdelta writes) produced on the author's machine, for engines the toolkit cannot rebuild. **Archive**: the manifest's `patch.url` names one **ZIP** file (Release asset `<game folder>-<language>-<pack id>-<revision>.zip`) with exactly one entry per touched file, named by its path + `.vcdiff` (`www/data/Map001.json.vcdiff`), stored or deflated, nothing else; `patch.sha256` / `size` are of the ZIP. ZIP over tar.gz: per-entry random access and in the .NET BCL and every CI tool; entries are looked up by the expected names, never extracted by the names inside, so no path in the archive reaches the disk; empty folder entries are allowed (_Binary patches_ in `docs/packs-and-registry.md`). Deltas are made with xdelta3 with `-S lzma` (its default) or `-S none`.
- **Data only.** Executable components (mod loaders like BepInEx, XUnity.AutoTranslator) come only from a maintainer-controlled whitelist in the registry: the manifest names the dependency and version, and the app downloads it from the official release and checks its hash (_Dependencies_ in `docs/packs-and-registry.md`). Registry CI rejects any executable file in a pack, and any dependency off the whitelist.
- **No original text, no game files.** Packs hold keys, translations, and recipes. The editor (later) shows original text from the player's local game copy.
- **Manifest** `manifest.json` (schema `schemas/pack-manifest.schema.json`, model `PackManifest` in `Core/Packs`) and **string table** `strings/strings.json.gz` (schema `schemas/string-table.schema.json`, gzip JSON). Unknown properties are ignored; a breaking change raises `schemaVersion`, and a newer one is refused with "update the app". Readers never throw for bad content; they return every problem as `PackFormatError`. Every field and rule: _Pack format details_ in `docs/packs-and-registry.md`.

## Registry

- **Layout**: `games/<game folder>/<language>/<pack-id>/` (`<game folder>` = the game ID with `:` → `-`, `steam-620`, `fp-<hex>`, since Windows forbids `:` in file names; `RegistryLayout`; `<language>` is a BCP 47 tag from `GameLanguages.Tags` in Core: the languages Steam supports, e.g. `ru`, `pt-BR`, `es-419`, `zh-Hans`) with `manifest.json` and `strings/` (later `audio/`). Several competing packs per game + language are allowed. Game versions are a supported-builds/hashes list in the manifest, not folders; updating a pack for a new game version is a new revision of the same pack.
- **Storage**: manifests and string tables live in git (reviewable in PRs). Binary parts (patches, later audio) are GitHub Release assets referenced from the manifest by URL + hash: the app uploads them to the `patches` release of the player's fork, and registry CI checks them and copies them to the registry's own `patches` release when it merges the PR (decided, task 60). Packs are stored compressed.
- **Client reads**: never clones and never calls the GitHub API for browsing. It fetches a static `index.json` from GitHub Pages and only the files it installs.
- **CI**: pack PRs (one pack folder, `status: alpha`) are validated by the CLI (`pack validate`, `index build`) and auto-merged without running PR code; maintenance PRs need a maintainer; *Publish index* deploys `index.json` to Pages. Details: _Registry CI and layout details_ in `docs/packs-and-registry.md`.
- **Lifecycle**: a pack PR auto-merges into `master` once CI passes (schema, hashes, no executables) with `status: alpha` (installable, shown with a warning). It becomes `verified` once N = 3 independent players confirm "works" (decided, task 64): `index build` works the status out on every run from the collected ratings, nothing is committed (manifests in git stay `alpha`); a new revision starts from zero. Maintainers grant or revoke `verified` for one revision in `policy/verified.json`.
- **Rating**: one GitHub Discussion per pack (category `Packs`, opened by the registry's Action, tied to the pack by the first line `<!-- ailocalpack-pack <game>/<language>/<id> -->`). 👍 reaction = like; templated comments = works / doesn't work reports and reviews. Voting requires GitHub sign-in. *Publish index* runs hourly (and on every merge): `ratings collect` reads the Discussions with the workflow's token, then `index build --ratings` publishes likes, counts and status.
- **Anti-abuse rules** (decided, task 64): counted = reports of the pack's current revision, one per account (its latest), not from the pack's authors or bots, from accounts at least 30 days old when they reported, and for a Steam pack from one of its listed builds; `verified` = at least 3 such "works" and more than "doesn't work". Likes leave out authors and bots. Details: _Ratings collection_ in `docs/packs-and-registry.md`.
- **Rating in the app** (decided, task 63): through GitHub's GraphQL API with the player's token; like = 👍 on the Discussion (toggle); a report is a comment whose first line is the marker `<!-- ailocalpack-report {"v":1,"verdict":"works"|"doesNotWork","game","language","pack","revision","build"} -->` (`PackReportComment`, the format `ratings collect` parses; only the marker counts), and a later report of the same revision edits the player's earlier one. Offered only after the player played with the installed revision (a session that started after the install ended), never to the pack's authors (Works / Doesn't work hidden); the game page's prompt comes once per revision (`Later` until the next session, twice at most). Pack Discussions live in the category `Packs` of the registry (and `registry-test`). Details: _Ratings_ in `docs/publishing.md`.
- **Legal**: disclaimer, a clear DMCA takedown process, and a per-game `opt-out` flag for studios that object (`LEGAL.md` and issue templates in the registry, decided in task 65). An opted-out game's packs are left out of the index and refused by CI, and the app blocks install, generation and publishing for it. Packs for pirated copies are not encouraged.
- **Licenses**: app under **Apache-2.0**; registry content under **CC BY-SA 4.0**.

## Publishing

- GitHub sign-in via **OAuth Device Flow** (decided, task 58): an OAuth App in the `ailocalpack` org, client ID baked in, no client secret anywhere; scope `public_repo` (fork, push, PR, Discussions; nothing private). The token lives only in the OS keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service over D-Bus; own code, no library); without a keychain (Linux with no keyring) the app stays signed out and explains, never stores the token in plain text. Sign-out deletes the token here; without a client secret the app can't revoke it on GitHub, so it links to the app's connections page. Details: `docs/publishing.md`.
- The app forks the registry via the API, commits the pack to a branch (`pack/<game folder>/<language>/<id>/rev-<N>`, Git Data API, no git on the machine), and opens a PR; publishing again pushes to the open PR's branch (decided, task 60). The player's login replaces the local placeholder author.
- Before publishing, it compares with existing packs and offers a new revision of an existing pack (own or someone else's) instead of a duplicate: a new revision keeps the original author first and credits the player as an author and the revision's `by`; recommended for the pack an update was made from, one without the player's build, or one translating less. Details: _Publish client_ in `docs/publishing.md`.
- **Publish flow** (decided, task 61): one click; dialogs only when needed: the first-time dialog (sign-in + license consent, remembered in `AppSettings.PublishLicenseAccepted`) while signed out or never accepted, the similar-pack dialog (the most relevant registry pack, the recommended choice preselected) when the index lists packs of the game + language. One publish at a time. A sent pack is *CI running* until the index lists its revision (*Published*); a failure before the PR is kept in memory with `Publish again`. Details: _Publish flow_ in `docs/publishing.md`.
- **PR tracking** (decided, task 62): the app follows the player's open pack PRs while it runs (window or tray): read at start, then when due (every minute at first, less later, 10 min while rejected; back-off after errors, a rate limit waited out), conditional requests (ETag), the player's token when signed in, else anonymous. CI failed = a check run on the head failed; the reason comes from its annotations (scope, status, patch, pack, registry) in plain words with what `Fix and resubmit` does: re-validate locally and push the pack to the same PR's branch rebuilt as one commit on the registry's head. Merged / closed are recorded in the local report and no longer read. Details: _PR tracking_ in `docs/publishing.md`.

## Open questions

- **Audio (phase 2)**: TTS engine (local open models like Piper/XTTS vs. cloud via the player's API key, or both via adapters); voice cloning of original actors (default: no, stock voices); audio codec and bitrate for compression.
