using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Codex;

/// <summary>
/// The OpenAI Codex CLI's headless command line (task 42; flags checked against <c>codex exec --help</c> of 0.167.0 and the
/// config reference, 2026-11-08): <c>codex exec --json --skip-git-repo-check --ignore-user-config --ignore-rules --color
/// never --cd &lt;dir&gt; --sandbox read-only|workspace-write -c approval_policy="never" -c web_search="disabled" -c
/// features.&lt;x&gt;=false… [-c sandbox_workspace_write.…] [-c mcp_servers.&lt;name&gt;.…] [-c developer_instructions="…"]
/// [--model &lt;m&gt;] [resume &lt;session&gt;] -</c>, the prompt on standard input (<c>-</c>).
/// <list type="bullet">
/// <item><b>Config</b> (decided): <c>-c</c> overrides over Codex's own defaults, the player's <c>config.toml</c> and
/// <c>.rules</c> ignored; not a generated <c>CODEX_HOME</c>, which also holds the sign-in (<c>--ignore-user-config</c>
/// keeps auth in the player's <c>CODEX_HOME</c>). Features are switched off with <c>-c features.x=false</c>, which an older
/// Codex ignores with a notice (<c>--disable x</c> refuses a name it doesn't know).</item>
/// <item><b>Sandbox</b>: Codex sandboxes writes and the network, not reads. No edit / command tools → <c>read-only</c>;
/// <see cref="AgentBuiltinTool.EditFiles"/> or <see cref="AgentBuiltinTool.RunCommands"/> → <c>workspace-write</c> with
/// the network off and only the working folder writable (<c>/tmp</c> and <c>$TMPDIR</c> excluded). Codex reads files
/// through its shell, so <see cref="AgentBuiltinTool.ReadFiles"/> keeps the shell tool (read-only commands in the
/// read-only sandbox); with neither reading nor commands the shell tools are off and only MCP tools remain.</item>
/// <item>Never asks (<c>approval_policy="never"</c>): what the sandbox refuses fails; MCP servers' tools are pre-approved
/// (<c>default_tools_approval_mode="approve"</c>) and the servers <c>required</c> (Codex stops if one can't start).</item>
/// <item>Off whatever the request: web search, apps / connectors, plugins, hooks, browser and computer use, image
/// generation, sub-agents, memories (<see cref="DisabledFeatures"/>).</item>
/// </list>
/// </summary>
public static partial class CodexCommandLine
{
    /// <summary>Features the app switches off for every run: anything acting outside the sandbox or the run.</summary>
    public static IReadOnlyList<string> DisabledFeatures { get; } =
    [
        "apps",
        "plugins",
        "remote_plugin",
        "hooks",
        "browser_use",
        "browser_use_external",
        "computer_use",
        "in_app_browser",
        "image_generation",
        "multi_agent",
        "memories",
        "skill_mcp_dependency_install",
        "tool_suggest",
    ];

    /// <summary>The run's arguments (after the installation's own).</summary>
    public static IReadOnlyList<string> Arguments(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tools = request.BuiltinTools;
        var writes = tools.Contains(AgentBuiltinTool.EditFiles) || tools.Contains(AgentBuiltinTool.RunCommands);
        var shell = tools.Contains(AgentBuiltinTool.ReadFiles) || tools.Contains(AgentBuiltinTool.RunCommands);
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--ignore-user-config",
            "--ignore-rules",
            "--color", "never",
            "--cd", request.WorkingDirectory,
            "--sandbox", writes ? "workspace-write" : "read-only",
        };

        void Set(string key, string tomlValue) => arguments.AddRange(["-c", $"{key}={tomlValue}"]);

        Set("approval_policy", Toml("never"));
        Set("web_search", Toml("disabled"));
        foreach (var feature in DisabledFeatures)
        {
            Set($"features.{feature}", "false");
        }

        if (!shell)
        {
            Set("features.shell_tool", "false");
            Set("features.unified_exec", "false");
        }

        if (writes)
        {
            Set("sandbox_workspace_write.network_access", "false");
            Set("sandbox_workspace_write.exclude_slash_tmp", "true");
            Set("sandbox_workspace_write.exclude_tmpdir_env_var", "true");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in request.McpServers)
        {
            if (!ServerName().IsMatch(server.Name) || !names.Add(server.Name))
            {
                throw new ProcessStartException($"\"{server.Name}\" can't be an MCP server's name (letters, digits, _ and -, each name once).");
            }

            var prefix = $"mcp_servers.{server.Name}";
            Set($"{prefix}.command", Toml(server.Command));
            Set($"{prefix}.args", "[" + string.Join(", ", server.Arguments.Select(Toml)) + "]");
            if (server.Environment.Count > 0)
            {
                Set($"{prefix}.env", "{" + string.Join(", ", server.Environment.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{Key(e.Key)} = {Toml(e.Value)}")) + "}");
            }

            Set($"{prefix}.default_tools_approval_mode", Toml("approve"));
            Set($"{prefix}.required", "true");
        }

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            Set("developer_instructions", Toml(request.SystemPrompt));
        }

        if (request.Model is { } model)
        {
            arguments.AddRange(["--model", model]);
        }

        if (request.ResumeSessionId is { } session)
        {
            arguments.AddRange(["resume", session]);
        }

        arguments.Add("-");
        return arguments;
    }

    /// <summary><paramref name="text"/> as a TOML basic string (quotes, backslashes and control characters escaped).</summary>
    public static string Toml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var toml = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': toml.Append("\\\""); break;
                case '\\': toml.Append("\\\\"); break;
                case '\n': toml.Append("\\n"); break;
                case '\r': toml.Append("\\r"); break;
                case '\t': toml.Append("\\t"); break;
                case '\b': toml.Append("\\b"); break;
                case '\f': toml.Append("\\f"); break;
                default:
                    if (char.IsControl(c))
                    {
                        toml.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        toml.Append(c);
                    }

                    break;
            }
        }

        return toml.Append('"').ToString();
    }

    private static string Key(string key) => ServerName().IsMatch(key) ? key : Toml(key);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ServerName();
}
