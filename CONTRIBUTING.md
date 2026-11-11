# Contributing to AI Local Pack

Thanks for your interest! AI Local Pack is open source (Apache-2.0) and welcomes contributions of every size: bug reports, fixes, translations of the app, new engine support, docs.

## Where things go

| You want to… | Where |
|---|---|
| Share or fix a **translation of a game** (a pack) | The [registry](https://github.com/ailocalpack/registry), not this repository. Pack PRs are validated by CI and merge on their own with status `alpha`. |
| Report a **bug** or suggest a **feature** in the app | An issue in this repository. |
| Report a **broken or harmful pack**, or ask for a game to be opted out | An issue in the [registry](https://github.com/ailocalpack/registry). |
| Fix or add a **translation of the app UI** | A PR to this repository (see [App UI translations](#app-ui-translations)). |
| Change the **code** or the **docs** | A PR to this repository. |
| Report a **security problem** | Privately, never in a public issue: *Report a vulnerability* on this repository's Security tab. |

For anything bigger than a small fix, open an issue first so we can agree on the approach before you spend time on it.

## Ground rules

These are the project's principles; PRs that break them can't be merged:

- **Install is deterministic and agent-free.** Only the app's own built-in code touches a player's game folder. Agent-written code never runs on other players' machines.
- **Packs are data only.** No executables, no original game text, no game files. Executable dependencies (BepInEx…) come only from the registry's whitelist, hash checked.
- **No server of our own.** The system is the app, GitHub and the player's own AI agent account. The project pays for no inference.
- **Every platform is first-class:** Windows, macOS and Linux (including Steam Deck). Keep store discovery, paths and process handling working on all three.
- **Games with anti-cheat stay blocked** unless the registry whitelists them.

## Getting started

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`). Optional: Claude Code or Codex for real generation runs (tests use a fake agent).

```sh
dotnet build                                   # whole solution; warnings are errors
dotnet test                                    # all tests
dotnet run --project src/AiLocalPack.Desktop   # the desktop app
dotnet run --project src/AiLocalPack.Cli -- <command>   # e.g. games list, toolkit detect <folder>
```

Keep your real data out of the way while developing:

- `AILOCALPACK_HOME=<dir>`: settings, data and cache under one folder (never touches autostart).
- `AILOCALPACK_STEAM_ROOT=<dir>`: use this folder instead of the real Steam install.
- `AILOCALPACK_INDEX_URL=tests/fixtures/registry`: read the sample registry instead of the public one.
- `AILOCALPACK_SECRETS_FILE=<path>`: keep secrets in a plain JSON file instead of the OS keychain (dev only).

No game at hand? `dotnet run --project src/AiLocalPack.Cli -- dev fixture rpgmaker|renpy|godot|unity|unreal|custom <dir>` writes a synthetic one. Generation without a real agent: `gen run <game> --fake-agent <recording.jsonl>` (recordings in `tests/fixtures/agents`).

## Read the spec first

`CLAUDE.md` holds the product principles and conventions; `docs/*.md` describe each component in detail (games, toolkit, packs and registry, install, generation, desktop app, foundations, publishing, CLI). Before changing a component, read its doc, and **update the doc in the same PR** when behaviour or a decision changes. `docs/status.md` lists what is implemented.

## Code conventions

- **Projects:** `Core` (domain, no UI), `Toolkit` (engines, fonts, validator), `Agents`, `Mcp`, `Pipeline`, `Cli`, `Desktop` (Avalonia). Keep dependencies pointing that way; Core never references UI.
- **Desktop:** MVVM with CommunityToolkit.Mvvm, DI graph in `Composition`, view-model-first navigation, UI updates from background events through `IUiDispatcher`, dialogs through `IDialogService`.
- **Robustness:** scanners, readers and services never throw for disk or network state; problems become typed results or are logged and skipped. Persisted JSON has a `schemaVersion`, separate DTOs and atomic writes (`AtomicFile`). Downloads are size + SHA-256 checked before they enter a cache.
- **Clock:** code that reads the time takes a `TimeProvider`.
- **App folders:** always through `AppPaths`; never build config / data / cache paths by hand.
- **Settings:** `AppSettings` is a positional record; every new setting is a constructor parameter with its default.
- **Packages:** versions only in `Directory.Packages.props`. The build fails on warnings and on any vulnerable package, so keep both at zero.
- **Naming:** players read **AI Local Pack** (`AppInfo.DisplayName`); **`ailocalpack`** stays the technical name (CLI, folders, IDs, files written into games). Don't rename technical names: it breaks existing installs and packs.
- **Agent prompts** are versioned files in `src/AiLocalPack.Pipeline/Prompts/<name>.v<N>.md`. A change that alters what the agent does is a new version file. Rendered prompts are snapshot-tested; refresh snapshots with `AILOCALPACK_UPDATE_SNAPSHOTS=1 dotnet test` and review the diff.
- Follow `.editorconfig`.

## Tests

Every change comes with tests. There is one `tests/AiLocalPack.<Project>.Tests` per `src` project, plus `tests/AiLocalPack.TestKit` with shared helpers (`TempDir`, `FakeHttpHandler`, `FakeClock`, `TestLogger<T>`, `SteamFixture`, `EngineFixture`, …).

- xunit.v3; assertions only through **AwesomeAssertions** (`x.Should()…`), never `Assert.*`.
- **AAA** with explicit `// Arrange`, `// Act`, `// Assert` comments; Act is one action; an expected exception is `var act = () => …;` then `act.Should().Throw<…>()`. One behaviour per test.
- Tests never touch the real Steam, keychain, app folders or a real agent: Desktop tests build services with `TestServices.Build()`, CLI tests run the CLI through `CliRunner` with a throwaway `AILOCALPACK_HOME`.
- UI tests run headless (`HeadlessUi.RunAsync`).
- Tests assert English UI text; a test that switches the UI language goes in `[Collection(typeof(UiLanguageSwitchCollection))]` and restores English in `finally`.

## App UI translations

The app ships in English, Deutsch, Español, Français, Italiano, 日本語, 한국어, Polski, Português (Brasil), Русский, Türkçe, Українська and 简体中文. The translations were made with an AI agent, so some strings read awkwardly or don't fit; fixes from native speakers are very welcome (the language picker in Settings → General links here).

**Fixing a string** (no build needed for a one-word fix; the GitHub web editor is enough):

1. Find the English text in `src/AiLocalPack.Desktop/Resources/Strings.resx`; its `name` is the key, and the `<comment>` says where it shows.
2. Change the `<value>` with the same key in `Strings.<lang>.resx` (`de`, `es`, `fr`, `it`, `ja`, `ko`, `pl`, `pt-BR`, `ru`, `tr`, `uk`, `zh-Hans`).
3. Keep every `{0}`, `{1:N0}`… placeholder (you may move them). Keep names that are never translated: AI Local Pack, Claude Code, Codex, GitHub, Steam, engine names, commands in backticks.
4. Keep labels short: the app is designed for 1280×800 (Steam Deck). If a button label changes, change the other strings that quote it too.
5. Players read **translation**, not "pack": a translation of a game into a language.
6. In the PR, say which screen you checked; a screenshot helps. `dotnet test` (or CI) checks keys, placeholders and plural forms.

For a whole language that reads badly, open an issue first: we'd rather agree on the terminology than review hundreds of single fixes.

**Rules for code changes:**

- No hard-coded user-visible text: strings live in `src/AiLocalPack.Desktop/Resources/Strings.resx` (English) and `Strings.<lang>.resx`. XAML uses `{l:Loc Key}`, code uses `Localizer` with `nameof(Strings.Key)`.
- A new key goes into **every** locale file in the same PR.
- Plurals are `Key_One` / `_Few` / `_Many` / `_Other` with exactly the CLDR forms of the language.
- **New UI language:** add `Strings.<lang>.resx` with every key, list the language in `UiLanguages.Supported`, add its plural rules to `PluralRules` if they're new. `ResourceFilesTests` check keys, placeholders and plural forms.
- Fixing an existing translation is a welcome small PR on its own.
- CLI output stays English only.

## Pull requests

1. Fork, branch from `master`, keep the PR focused on one change.
2. `dotnet build` with no warnings and `dotnet test` green on your machine. If the change is platform-specific, say which OSes you checked it on.
3. Update the matching `docs/*.md` (and `CLAUDE.md` for principles and conventions).
4. For UI changes, add a screenshot to the PR description.
5. Describe *what* changed and *why*. Commit messages: a short summary line, details below it.

By contributing you agree that your contribution is licensed under the project's license: Apache-2.0 for this repository, CC BY-SA 4.0 for registry content.

## Code of conduct

Be kind and constructive. Harassment and personal attacks aren't tolerated in issues, PRs or discussions. Please don't ask for, share or link pirated copies of games.
