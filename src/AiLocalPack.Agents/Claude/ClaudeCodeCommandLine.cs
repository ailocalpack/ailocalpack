using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Claude Code's headless command line (task 41; flags checked against the CLI reference and <c>claude --help</c> of
/// 2.1.319, 2026-11-08):
/// <c>claude -p --output-format stream-json --verbose --input-format text --restricted --disable-slash-commands
/// --permission-mode dontAsk --permission-prompts none --strict-mcp-config [--mcp-config &lt;file&gt;] --tools &lt;list&gt;
/// [--allowedTools &lt;rules&gt;] [--append-system-prompt-file &lt;file&gt;] [--model &lt;m&gt;] [--effort &lt;e&gt;] [--resume &lt;id&gt;]</c>, the prompt on
/// standard input.
/// <list type="bullet">
/// <item><c>--restricted</c> confines the built-in file tools to the working folder (the generation working copy), drops
/// the tools that run code unless <c>--tools</c> names them and ignores the player's user and project settings (hooks,
/// plugins, permission rules); <c>--safe-mode</c> isn't used because it also drops <c>--mcp-config</c> servers.</item>
/// <item><c>--tools</c> = only the built-in tools the request allows (<c>""</c> = none); <c>--allowedTools</c> pre-approves
/// them and every tool of each MCP server (<c>mcp__&lt;name&gt;</c>); <c>dontAsk</c> + <c>--permission-prompts none</c>
/// denies anything else instead of waiting for an answer nobody gives.</item>
/// <item><c>--strict-mcp-config</c>: only the run's MCP servers, none of the player's.</item>
/// <item><b>Commands</b> (<see cref="AgentBuiltinTool.RunCommands"/>, task 57): <c>--settings &lt;file&gt;</c> with
/// <see cref="SandboxSettings"/>: Bash runs in Claude Code's sandbox, which lets commands write only the working folder
/// (and the temporary folder, which <see cref="AgentSandbox"/> moves inside it) and reach no network host;
/// <c>failIfUnavailable</c> + <c>allowUnsandboxedCommands: false</c>, so a sandbox that can't start (Linux without
/// bubblewrap) runs no command at all instead of running it unconfined (checked with 2.1.322 on macOS: without them a
/// sandbox that failed to start ran the commands unconfined).</item>
/// <item>The MCP config and the system prompt go in files (<see cref="McpConfigFileName"/>,
/// <see cref="SystemPromptFileName"/>) so no quoting or length limit applies (cmd.exe takes 8 191 characters).</item>
/// </list>
/// </summary>
public static partial class ClaudeCodeCommandLine
{
    /// <summary>The oldest Claude Code with every flag above (<c>--permission-prompts</c>: 2.1.259; <c>--restricted</c>: 2.1.248).</summary>
    public static readonly Version MinimumVersion = new(2, 1, 259);

    public const string McpConfigFileName = "mcp.json";

    public const string SystemPromptFileName = "system-prompt.md";

    public const string SettingsFileName = "settings.json";

    /// <summary>The <c>--settings</c> of a run with commands: Bash only in the sandbox, no network, never unconfined.</summary>
    public const string SandboxSettings = """
        {
          "sandbox": {
            "enabled": true,
            "failIfUnavailable": true,
            "autoAllowBashIfSandboxed": true,
            "allowUnsandboxedCommands": false,
            "network": { "allowedDomains": [], "strictAllowlist": true }
          }
        }
        """;

    /// <summary>The run's arguments (after the installation's own), given where its MCP config and system prompt files are.</summary>
    public static IReadOnlyList<string> Arguments(AgentRunRequest request, string? mcpConfigFile, string? systemPromptFile, string? settingsFile = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--input-format", "text",
            "--restricted",
            "--disable-slash-commands",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            "--strict-mcp-config",
        };
        if (mcpConfigFile is not null)
        {
            arguments.AddRange(["--mcp-config", mcpConfigFile]);
        }

        if (settingsFile is not null)
        {
            arguments.AddRange(["--settings", settingsFile]);
        }

        var tools = BuiltinToolNames(request.BuiltinTools);
        arguments.AddRange(["--tools", string.Join(',', tools)]);
        var allowed = tools.Concat(request.McpServers.Select(s => "mcp__" + s.Name)).ToList();
        if (allowed.Count > 0)
        {
            arguments.AddRange(["--allowedTools", string.Join(',', allowed)]);
        }

        if (systemPromptFile is not null)
        {
            arguments.AddRange(["--append-system-prompt-file", systemPromptFile]);
        }

        if (request.Model is { } model)
        {
            arguments.AddRange(["--model", model]);
        }

        if (request.Effort is { } effort)
        {
            arguments.AddRange(["--effort", effort.ToString().ToLowerInvariant()]);
        }

        if (request.ResumeSessionId is { } session)
        {
            arguments.AddRange(["--resume", session]);
        }

        return arguments;
    }

    /// <summary>Claude Code's built-in tools for the allowed kinds, in a fixed order.</summary>
    public static IReadOnlyList<string> BuiltinToolNames(IReadOnlySet<AgentBuiltinTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var names = new List<string>();
        if (tools.Contains(AgentBuiltinTool.ReadFiles))
        {
            names.AddRange(["Read", "Glob", "Grep"]);
        }

        if (tools.Contains(AgentBuiltinTool.EditFiles))
        {
            names.AddRange(["Edit", "Write"]);
        }

        if (tools.Contains(AgentBuiltinTool.RunCommands))
        {
            names.Add("Bash");
        }

        if (tools.Contains(AgentBuiltinTool.WebSearch))
        {
            names.AddRange(["WebSearch", "WebFetch"]);
        }

        return names;
    }

    /// <summary>
    /// The <c>--mcp-config</c> file: <c>{"mcpServers": {name: {"type": "stdio", command, args, env}}}</c>. Throws
    /// <see cref="ProcessStartException"/> for a name Claude Code can't prefix tools with (letters, digits, <c>_</c>, <c>-</c>)
    /// or a name given twice.
    /// </summary>
    public static string McpConfig(IReadOnlyList<AgentMcpServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            json.WriteStartObject("mcpServers");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in servers)
            {
                if (!ServerName().IsMatch(server.Name) || !names.Add(server.Name))
                {
                    throw new ProcessStartException($"\"{server.Name}\" can't be an MCP server's name (letters, digits, _ and -, each name once).");
                }

                json.WriteStartObject(server.Name);
                json.WriteString("type", "stdio");
                json.WriteString("command", server.Command);
                json.WriteStartArray("args");
                foreach (var argument in server.Arguments)
                {
                    json.WriteStringValue(argument);
                }

                json.WriteEndArray();
                json.WriteStartObject("env");
                foreach (var (name, value) in server.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    json.WriteString(name, value);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The version in <c>claude --version</c> output (<c>2.1.319 (Claude Code)</c> → <c>2.1.319</c>), else <c>null</c>.</summary>
    public static string? ParseVersion(string output) => AgentPrograms.ParseVersion(output);

    /// <summary>Whether <paramref name="version"/> (as <see cref="ParseVersion"/> gives it) has every flag the app passes.</summary>
    public static bool IsSupported(string? version) => AgentPrograms.IsAtLeast(version, MinimumVersion);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ServerName();
}
