# Security policy

## Supported versions

Only the latest release of AI Local Pack (the desktop app and the `ailocalpack` CLI) gets security fixes. The app updates itself from GitHub Releases, so a fix reaches players with the next release.

## Reporting a vulnerability

Report it privately, never in a public issue, pull request or Discussion: open the [Security tab](https://github.com/ailocalpack/ailocalpack/security) of this repository and click **Report a vulnerability**. Only the maintainers see the report.

Please include what is affected (app, CLI, toolkit MCP server, registry CI), the version and OS, steps to reproduce and what an attacker gains.

We answer within 7 days, keep you posted while we fix it, and credit you in the advisory unless you'd rather not be named.

## What is in scope

- The desktop app and the CLI: the install transaction, pack and patch reading, downloads and their hash checks, the keychain, GitHub sign-in.
- Packs reaching a player's machine: anything that makes a pack run code, write outside the game folder or skip the registry's dependency whitelist.
- The toolkit MCP server and the agent sandbox: an agent writing into the game folder or escaping its workspace.
- Registry CI ([ailocalpack/registry](https://github.com/ailocalpack/registry)): a pack PR running its own code or getting an executable merged.

Not in scope: the AI agents themselves (Claude Code, Codex and others; report to their vendors), a player's own models and servers, and games' anti-cheat.

Content problems in the registry (a copyright takedown, a studio opting out) go through the registry's `LEGAL.md`, not here.
