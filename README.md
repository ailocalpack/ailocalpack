<p align="center"><img src="assets/branding/app-icon-512.png" width="128" alt=""></p>

<h1 align="center">AI Local Pack</h1>

<p align="center">
  <b>Play your games in your language.</b><br>
  Install a translation other players made, or create one with AI in a few clicks.<br>
  Free and open source, for Windows, macOS and Linux.
</p>

<p align="center">
  <a href="#download"><b>Download</b></a> ·
  <a href="#how-to-use-it">How to use it</a> ·
  <a href="#faq">FAQ</a> ·
  <a href="https://github.com/ailocalpack/registry">Translations</a> ·
  <a href="#privacy">Privacy</a> ·
  <a href="#code-signing-policy">Code signing policy</a>
</p>

<p align="center"><img src="assets/screenshots/library.png" width="860" alt="The AI Local Pack library: your Steam games with their covers, engines and whether a translation exists for your language"></p>

## What it does

- **Finds your games.** Your Steam library shows up on its own. Games from elsewhere: add their folder.
- **Shows which games have a translation** into your language and installs one with a single click.
- **Creates a translation when there is none.** An AI agent (Claude Code or Codex, on your own subscription) translates the game's text. You review the names and terms, then play.
- **Shares it with everyone.** Publish your translation in one click so other players get it too.
- **Undo any time.** Every file a translation changes is backed up first. Uninstall it and the game is as it was.

## Download

| Your computer | Installer | Without installing |
|---|---|---|
| **Windows** (most PCs) | [Download `.exe`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-win-x64-Setup.exe) | [Portable `.zip`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-win-x64-Portable.zip) |
| **Windows on ARM** (Snapdragon laptops) | [Download `.exe`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-win-arm64-Setup.exe) | [Portable `.zip`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-win-arm64-Portable.zip) |
| **Mac with Apple chip** (M1, M2, M3…) | [Download `.pkg`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-osx-arm64-Setup.pkg) | [Portable `.zip`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-osx-arm64-Portable.zip) |
| **Mac with Intel** | [Download `.pkg`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-osx-x64-Setup.pkg) | [Portable `.zip`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-osx-x64-Portable.zip) |
| **Linux** (64-bit) | [Download `.AppImage`](https://github.com/ailocalpack/ailocalpack/releases/latest/download/AiLocalPack-linux-x64.AppImage) | the AppImage needs no installing |

The links always point to the latest version. All versions and what changed: [Releases](https://github.com/ailocalpack/ailocalpack/releases). How releases are signed: [Code signing policy](#code-signing-policy). What the app connects to: [Privacy](#privacy).

**Which Mac do I have?** Apple menu → *About This Mac*. *Chip: Apple M…* means Apple chip; *Processor: Intel…* means Intel.

The app updates itself: when a new version is ready, it offers *Restart to update*.

**Installing for the first time?** The app isn't code-signed yet, so Windows and macOS warn about it once. Step-by-step: [How do I install it?](#how-do-i-install-it) in the FAQ.

## How to use it

### Install a translation

1. Open the app. Your Steam games appear. Another game? Click **Folder** and pick the folder it is installed in.
2. Choose your language at the top: **Translate to …**. Games that have a translation are marked *Translation available*.
3. Open a game, pick a translation and click **Install**. Then play as usual.

To go back to the original, open the game and click **Uninstall**.

<p align="center"><img src="assets/screenshots/game-page.png" width="760" alt="A game page: the Text tab lists translations into the chosen language, the Create with AI tab makes a new one"></p>

### Create a translation with AI

No translation for your game yet? Open it and click **Create with AI**.

- You need **Claude Code** (with a Claude Pro or Max subscription) or **Codex** (with a ChatGPT Plus or Pro subscription). The app installs the agent for you if it's missing and opens its sign-in. AI Local Pack itself costs nothing; the AI work counts against your own subscription.
- Before starting, the app shows how big the job is. A large game can take hours, and if your subscription's limit runs out, the app waits and continues on its own.
- Halfway through, you can check the list of names and terms (characters, places, how characters address each other) and fix anything you'd translate differently.
- When it's done, click **Install & play**.

### Share it

After you've tried your translation in the game, click **Publish**. The first time, the app asks you to sign in with a free GitHub account. Other players then see your translation in their app, can rate it, and it becomes **verified** once three players confirm it works.

## FAQ

### How do I install it?

Download the file for your computer from [Download](#download). **The app isn't code-signed yet** (signing is being set up, see [Code signing policy](#code-signing-policy)), so your system doesn't know who made it and warns you once. That's expected for now; the steps below get past it. Download only from this repository's [Releases](https://github.com/ailocalpack/ailocalpack/releases): every file there is built from this code by its public [release workflow](.github/workflows/release.yml).

**Windows** (`Setup.exe`)

1. Your browser may say the file *isn't commonly downloaded*: choose **Keep** (Edge: **…** → **Keep** → **Keep anyway**).
2. Run `AiLocalPack-win-x64-Setup.exe`. Windows shows *Windows protected your PC*: click **More info** → **Run anyway**.
3. The installer needs no admin rights: it installs for your user and starts the app. Next time, start *AI Local Pack* from the Start menu.

Portable `.zip` instead: right-click the ZIP → *Properties* → tick **Unblock** → *OK*, extract it anywhere and run `AiLocalPack.exe` (answer *Windows protected your PC* the same way). If Windows 11's **Smart App Control** is on, it blocks unsigned apps with no *Run anyway* button; until the app is signed it can't run there.

**macOS** (`.pkg`)

1. Double-click the `.pkg`. macOS says it *can't be opened* or *can't be checked for malicious software*: click **Done** (or **OK**).
2. Open **System Settings → Privacy & Security**, scroll down to *"AiLocalPack…" was blocked* and click **Open Anyway**, then confirm with your password.
3. Go through the installer. *AI Local Pack* appears in *Applications*. If macOS asks again on the first start, repeat step 2 for the app.

Portable `.zip` instead: double-click it to extract, move *AI Local Pack* to *Applications* and open it; allow it in **Privacy & Security → Open Anyway** as above. (In Terminal, `xattr -dr com.apple.quarantine "/Applications/AI Local Pack.app"` does the same.)

**Linux** (`.AppImage`)

1. Allow the file to run: right-click → *Properties* → *Allow executing file as program*, or `chmod +x AiLocalPack-linux-x64.AppImage`.
2. Double-click it (or run `./AiLocalPack-linux-x64.AppImage`). Nothing to install; keep the file where you like.
3. If nothing happens, install FUSE 2: `sudo apt install libfuse2` on Ubuntu 22.04 / Debian (`libfuse2t64` on Ubuntu 24.04 and later), `fuse-libs` on Fedora. Or run it without FUSE: `./AiLocalPack-linux-x64.AppImage --appimage-extract-and-run`.

Updates come through the app itself (*Restart to update*) and don't ask again.

**Why isn't it signed? Is that safe?**
Code signing costs money and paperwork that this free project is still setting up: Windows signing through [SignPath Foundation](https://signpath.org/) and an Apple Developer ID for macOS. Until then the warnings only mean "unknown publisher", not that something wrong was found. The code is open, and releases are built on GitHub's servers from it. Once signing is on, the warnings go away with the next update.

**Is it free?**
Yes, the app and every translation in it. Only creating a new translation with AI uses your own Claude or ChatGPT subscription.

**Which games work?**
Most games made with Unity, Unreal Engine 4 / 5, RPG Maker MV / MZ, Ren'Py and Godot. The app shows a game's engine on its card. For other engines the AI can still try to make a translation, with less certainty.

**Is it safe for my game?**
Installing a translation runs only the app's own code: no AI and no downloaded scripts touch your game. Translations contain only text, never programs, and every file they change is backed up so you can undo. The AI works on a copy of the game, never on the game itself.

**Can I get banned?**
Games with anti-cheat (Easy Anti-Cheat, BattlEye, Vanguard and others) are blocked: the app won't change them, unless the game officially allows mods.

**What happens when the game updates?**
A Steam update can overwrite the translation. The app notices and puts it back if it still fits the new version. If it doesn't, the app tells you and offers a newer version of the translation, or to translate only the new text with AI.

**Which languages?**
Any language Steam supports, as the language you translate games into. The app itself is in English, Deutsch, Español, Français, Italiano, 日本語, 한국어, Polski, Português (Brasil), Русский, Türkçe, Українська and 简体中文.

**Steam Deck? Epic, GOG?**
Planned. For now: Steam games, plus any game folder you add yourself (so Epic and GOG games work if you add their folder).

**A translation is wrong or doesn't work.**
Rate it in the app (*Doesn't work* with a short comment), or fix it with AI and publish the fix as its new version.

**How do I remove the app?**
First uninstall the translations you no longer want (each game's page). Then: Windows: *Settings → Apps*, AI Local Pack → *Uninstall*. macOS: move *AI Local Pack* from *Applications* to the Trash. Linux: delete the AppImage.

**Where do the translations come from?**
From players like you: they live in the public [translation registry](https://github.com/ailocalpack/registry) on GitHub. There is no server of our own: just the app, GitHub and your own AI subscription.

## Status

Early version: things may still break. Planned next: voice-over and translated textures in games, Steam Deck, more stores, and your own API key or a local AI model. Found a bug? [Open an issue](https://github.com/ailocalpack/ailocalpack/issues).

## For developers

The app is C# / .NET 10 with [Avalonia UI](https://avaloniaui.net/). Building, tests and conventions: [CONTRIBUTING.md](CONTRIBUTING.md). The command-line tool (also used by the registry's CI) ships with each release as `ailocalpack-cli-<version>-<os>`; every feature with its commands: [docs/status.md](docs/status.md). Component specs live in [docs/](docs).

- **App UI translations**, bug reports and code go to this repository ([CONTRIBUTING.md](CONTRIBUTING.md)).
- **Game translations** go to the [registry](https://github.com/ailocalpack/registry) as pull requests (the app does this for you).

## Privacy

AI Local Pack collects no telemetry and has no server of its own. It sends none of your data anywhere unless you ask it to. It connects only to:

- **GitHub Pages** (`ailocalpack.github.io`): the list of translations, read at start and from time to time.
- **GitHub** (`github.com`, `raw.githubusercontent.com`): the translation files you install, app updates (checked at start and every 6 hours), fonts a translation needs, and the official downloads of Claude Code / Codex when you ask the app to install one.
- **Steam's image servers** (`steamstatic.com`): game covers.
- **The GitHub API** (`api.github.com`), only after you sign in with GitHub: publishing your translations, rating translations and following your pull requests. Your GitHub token is kept only in your system's keychain.
- **Your AI agent** (Claude Code from `downloads.claude.ai`, or Codex), only when you create a translation. The agent sends the game's text to its vendor (Anthropic or OpenAI) under that vendor's privacy policy and your own account.

What you publish (your translation and your GitHub user name as its author, ratings and comments) is public on GitHub.

## Code signing policy

Windows releases get free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). (Signing is being set up; until it is, releases are unsigned, see [How do I install it?](#how-do-i-install-it).)

Only the app built from this repository by its [release workflow](.github/workflows/release.yml) is signed: `AiLocalPack.exe`, the `AiLocalPack.*.dll` files and the `Setup.exe` installer. Third-party libraries are not signed by us.

- **Committers and reviewers**: [members of the ailocalpack organization](https://github.com/orgs/ailocalpack/people)
- **Approvers**: [owners of the ailocalpack organization](https://github.com/orgs/ailocalpack/people?query=role%3Aowner)

Every signing request is approved by hand by an approver. All team members use two-factor authentication on GitHub and SignPath.

## Licenses

The app: Apache-2.0. Translations in the registry: CC BY-SA 4.0.

Translations contain no game files. Please own the games you translate. Studios that object to fan translations of their game can ask for it to be opted out of the registry ([LEGAL.md](https://github.com/ailocalpack/registry/blob/master/LEGAL.md)).
