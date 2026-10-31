# ailocalizer

Open-source, cross-platform (Windows, macOS, Linux) desktop app that lets a player **install** a community localization for any game they own, or **generate** a new one with an AI agent and **publish** it for others.

Status: pre-code. The stack is not chosen yet; it is the next decision (separate brainstorm). Until it lands, treat this file as the product spec and keep it current as decisions are made: record each settled decision under _Decisions_, and remove the matching item from _Open questions_.

## Core flow

1. **Discover**: on launch, the app scans installed store launchers (Steam, EA app/Origin, Epic, GOG, others) and builds the player's game library. The player can also add a game by pointing at its folder.
2. **Identify**: each game gets a stable **game identity**, so a localization made on one machine matches the same game on another (see _Open questions_).
3. **Browse**: show the localizations available for the player's language (default: OS language), with search across other languages.
4. **Install**: one click puts the pack's files into the game folder, so the player just launches the game and it works. Installing must be reversible: back up every file it overwrites and support uninstall.
5. **Generate** (when no suitable pack exists for the game, language, or game version): offer to create one with an AI agent:
   - The player picks an agent (Claude Code, Codex, others) and signs in with **their own account**; the app installs the agent locally if it is missing.
   - The player picks what to translate: **text**, **audio**, or both.
   - The agent runs locally against the game files: finds the localizable resources, extracts them, translates them, and repacks them into a pack.
6. **Publish**: offer to share the generated pack with the community through the registry.

## Registry

There is no backend server; that is a hard constraint. Shared packs live in a separate public git repository, the **registry**, apart from the app's own source repo.

Proposed layout (not final):

```
games/<game-id>/<game-version>/<language>/
    text/
    audio/
```

- Publishing = the app has the player sign in to the git host, then opens a GitHub pull request with the new pack, but only if it differs from the packs already there.
- **Pack status**:
  - **alpha**: published but unreviewed. Visible and installable, shown with a warning.
  - **verified**: someone ran it in the game, confirmed it works, and it was merged to the main branch.
- **Rating**: packs get likes or some other rating. The mechanism is not designed yet; it must also work without a server.

## Constraints

- Every platform is first-class: Windows, macOS, Linux. Store discovery, file paths, and agent installation all differ per OS.
- No server of our own: the app, the git host, and the player's AI agent account are the whole system.
- AI usage goes through the player's own agent subscription or account; the project pays for no inference.
- The app is open source.

## Domain terms

- **Game**: an installed title, found through a store or added manually.
- **Game identity**: the stable ID used to match a game across machines and stores.
- **Game version**: the specific build; packs can break between versions.
- **Pack**: one localization of one game version into one language, made of text and/or audio parts.
- **Agent**: an external AI coding agent (Claude Code, Codex, etc.) that the app drives to generate a pack.
- **Registry**: the public git repo of packs.

## Decisions

- **Git host: GitHub.** The registry is a GitHub repo; publishing uses GitHub sign-in and opens a pull request.
- **Packs are stored compressed.** Heavy content (especially audio) must be compacted before it reaches the registry, to keep download traffic and repo size down. The method is still open (see _Open questions_).

## Open questions

- **Stack**: desktop framework and language, packaging, and auto-update.
- **Game identity**: store app IDs (Steam appid, etc.) vs. executable hashes vs. a manifest of file hashes; how to match manually added games; how to detect the game version.
- **Registry layout**: is game version a folder level, or a compatibility range in a pack manifest? What goes in the pack manifest (author, agent used, status, supported versions, checksums)?
- **Heavy files**: how to compress packs (audio codec and bitrate, archive format, shipping diffs instead of full files) and where large binaries live (plain git, Git LFS with its GitHub quotas, GitHub Release assets). Players should download only the parts they install (text only, a single language).
- **Rating without a server**: reactions on PRs/issues, a stars file updated through PRs, or something else.
- **Review process**: who verifies packs, and how verification is recorded.
- **Agent integration**: how the app drives each agent (CLI, SDK), which instructions or tools it gives the agent for unpacking game formats, and how progress and failures are reported to the player.
- **Legal**: which game-derived content is safe to redistribute in the registry (e.g. diffs/patches vs. full asset files).
