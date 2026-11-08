# Codex exec JSONL transcripts

Output of `codex exec --json …` (Codex CLI 0.167.0, 2026-11-08) with the flags of `CodexCommandLine`, read by
`CodexParser` tests. Paths were shortened (`/work`, `/mcp`), the user name, Cloudflare ray and request IDs replaced.

- `success.jsonl`: "List the files here" in the read-only sandbox (one `command_execution`, then the answer).
- `mcp-tool-call.jsonl`: a call of a test MCP server's `count_strings` tool, shell tools off.
- `file-change.jsonl`: an edit of `a.txt` (`file_change`) in the workspace-write sandbox; writing outside it failed.
- `signed-out.jsonl`: an empty `CODEX_HOME` (`Reconnecting… n/5` retries, then `401 Unauthorized` and `turn.failed`).
- `limit-reached.jsonl`: **made by hand** from `success.jsonl` and the wording of `UsageLimitReachedError` in
  `codex-rs/protocol/src/error.rs` (a real limit can't be reached on purpose); the reset time is local time.
