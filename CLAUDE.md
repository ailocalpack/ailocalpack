# ailocalpack

Open-source, cross-platform (Windows, macOS, Linux) desktop app that lets a player **install** a community localization for any game they own, or **generate** a new one with an AI agent and **publish** it for others.

Status: solution skeleton and app shell (navigation, chrome, theme) in place; no features yet. Treat this file as the product spec and keep it current: record each settled decision in its section, and remove the matching item from _Open questions_.

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
- **Desktop app structure**: MVVM with CommunityToolkit.Mvvm, `Microsoft.Extensions.DependencyInjection` (graph in `Composition`), view-model-first navigation (`INavigationService` back stack with Library as root; page views mapped by `DataTemplate`s in `App.axaml`, no reflection view locator). Back: chrome button, Esc, Alt+←, mouse back button. Theme: Fluent + own tokens (`Styles/Theme.axaml`, colors from `design/drafts`), follows the OS.
- **Logging**: Serilog rolling file via `Microsoft.Extensions.Logging` (daily, 7 files, 10 MB cap); unhandled exceptions (AppDomain, UI dispatcher, unobserved tasks) are logged.
- **App UI is multilingual from the start**: strings in resource files, top ~10 player languages, initially AI-translated; community fixes via PRs. Language follows the OS.

## Repositories

Both live in the GitHub organization **`ailocalpack`** (not a personal account: the index URL is baked into the app and must never move).

- **`ailocalpack/ailocalpack`** (this repo): one .NET solution: Avalonia UI, core, toolkit, MCP server, agent adapters, and a CLI used by registry CI to validate packs.
  - Layout: `AiLocalPack.slnx`; `src/AiLocalPack.{Core,Toolkit,Agents,Mcp,Cli,Desktop}`; `tests/AiLocalPack.*.Tests` (xunit.v3 on Microsoft.Testing.Platform).
  - Tests: one `tests/AiLocalPack.<Project>.Tests` per `src` project, plus `tests/AiLocalPack.TestKit` (shared helpers: `TempDir`, `FakeHttpHandler`, `FakeClock`; referenced by every test project via `tests/Directory.Build.props`). Run with `dotnet test` from the root.
  - Test style: assertions only through **AwesomeAssertions** (`x.Should()...`, global using in `tests/Directory.Build.props`), never xunit `Assert.*`. Every test follows **AAA** with explicit `// Arrange`, `// Act`, `// Assert` comments; the Act step is one action, and an expected exception is captured as `var act = () => ...;` then asserted with `act.Should().Throw<...>()`. One behaviour per test.
  - Time: code that reads the clock takes a `TimeProvider` (default `TimeProvider.System`); tests pass `FakeClock`.
  - UI tests: Avalonia headless via `HeadlessUnitTestSession` and plain `[Fact]` (`HeadlessUi.RunAsync` in Desktop.Tests). `Avalonia.Headless.XUnit` 12.1.x is built against xunit.v3 3.x and breaks discovery on xunit.v3 4.x; switch to `[AvaloniaFact]` once it supports v4.
  - .NET 10 (`global.json`). Shared settings in `Directory.Build.props`: `TreatWarningsAsErrors` and NuGet audit (`all`, level `low`) fail the build on any vulnerable package, transitive included. Versions only in `Directory.Packages.props` (central package management; pin patched transitive versions there).
- **`ailocalpack/registry`**: packs, JSON schemas, GitHub Actions, GitHub Pages index at `https://ailocalpack.github.io/registry/index.json`.

## Game identity

- Game ID = store ID (`steam:1091500`). Manually added games get a fingerprint from hashes of the main executable and key data files.
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

- **Layout**: `games/<game-id>/<language>/<pack-id>/` with `manifest.json` and `strings/` (later `audio/`). Several competing packs per game + language are allowed. Game versions are a supported-builds/hashes list in the manifest, not folders; updating a pack for a new game version is a new revision of the same pack.
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
