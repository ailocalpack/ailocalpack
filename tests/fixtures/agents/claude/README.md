# Claude Code stream-json transcripts

Output of `claude -p --output-format stream-json --verbose …` (Claude Code 2.1.319, 2026-11-08) with the flags of
`ClaudeCodeCommandLine`, read by `ClaudeCodeParser` tests. Paths were shortened (`/work`, `/mcp`), lists of the
recording machine's skills, plugins and commands emptied.

- `success.jsonl`: "List the files here", Read / Glob / Grep allowed (a Glob call, then the answer).
- `mcp-tool-call.jsonl`: a call of a test MCP server's `count_strings` tool (result as text blocks).
- `denied-outside-folder.jsonl`: a Read of `/etc/hosts`, refused by `--restricted` (`is_error` tool result).
- `signed-out.jsonl`: an empty `CLAUDE_CONFIG_DIR` (assistant `error: authentication_failed`).
- `limit-reached.jsonl`: **made by hand** from the lines above (a real limit can't be reached on purpose): the
  `rate_limit_event` with `status: rejected` and `resetsAt` (Unix seconds), then the assistant `error: rate_limit`
  and an error result, as the Agent SDK reference describes them.
