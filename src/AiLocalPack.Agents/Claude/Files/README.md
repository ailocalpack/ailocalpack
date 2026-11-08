# Claude Code release signing key

`claude-code-release-key.asc` is Anthropic's public key that signs every Claude Code release manifest
(`https://downloads.claude.ai/claude-code-releases/<version>/manifest.json.sig`). The app installs Claude Code only
from a manifest this key signed (task 43, `ClaudeCodeDistribution`), then checks the binary's SHA-256 from it.

- Source: `https://downloads.claude.ai/keys/claude-code.asc`, fetched 2026-11-08.
- User ID: `Anthropic Claude Code Release Signing <security@anthropic.com>`.
- Fingerprint (pinned in `ClaudeCodeDistribution.ReleaseKeyFingerprint`): `31DD DE24 DDFA B679 F42D 7BD2 BAA9 29FF 1A7E CACE`.
- RSA 4096, created 2026-03-30, no expiry; signatures are SHA-512.

When Anthropic rotates the key, replace the file and the pinned fingerprint together, after checking the new key
against Anthropic's documentation.
