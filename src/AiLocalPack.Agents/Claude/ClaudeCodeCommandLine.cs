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
/// [--allowedTools &lt;rules&gt;] [--append-system-prompt-file &lt;file&gt;] [--model &lt;m&gt;] [--resume &lt;id&gt;]</c>, the prompt on
/// standard input.
/// <list type="bullet">
/// <item><c>--restricted</c> confines the built-in file tools to the working folder (the generation working copy), drops
/// the tools that run code unless <c>--tools</c> names them and ignores the player's user and project settings (hooks,
/// plugins, permission rules); <c>--safe-mode</c> isn't used because it also drops <c>--mcp-config</c> servers.</item>
/// <item><c>--tools</c> = only the built-in tools the request allows (<c>""</c> = none); <c>--allowedTools</c> pre-approves
/// them and every tool of each MCP server (<c>mcp__&lt;name&gt;</c>); <c>dontAsk</c> + <c>--permission-prompts none</c>
/// denies anything else instead of waiting for an answer nobody gives.</item>
/// <item><c>--strict-mcp-config</c>: only the run's MCP servers, none of the player's.</item>
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

    /// <summary>The run's arguments (after the installation's own), given where its MCP config and system prompt files are.</summary>
    public static IReadOnlyList<string> Arguments(AgentRunRequest request, string? mcpConfigFile, string? systemPromptFile)
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
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
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

    /// <summary>
    /// The process running <paramref name="executable"/> with <paramref name="arguments"/>: directly, or on Windows a batch
    /// file (npm's <c>claude.cmd</c>) through <c>cmd.exe</c> with its own escaping (<see cref="WindowsCommandLine"/>).
    /// </summary>
    public static ProcessSpec Process(string executable, IReadOnlyList<string> arguments, OsKind os, ProcessSpec? template = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var spec = (template ?? new ProcessSpec(executable, [])) with { FileName = executable, Arguments = arguments, RawArguments = null };
        return os == OsKind.Windows && WindowsCommandLine.IsBatchFile(executable)
            ? WindowsCommandLine.ForBatchFile(executable, arguments, spec)
            : spec;
    }

    /// <summary>The version in <c>claude --version</c> output (<c>2.1.319 (Claude Code)</c> → <c>2.1.319</c>), else <c>null</c>.</summary>
    public static string? ParseVersion(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = VersionText().Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>Whether <paramref name="version"/> (as <see cref="ParseVersion"/> gives it) has every flag the app passes.</summary>
    public static bool IsSupported(string? version)
    {
        if (version is null)
        {
            return false;
        }

        var core = version.Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) && parsed >= MinimumVersion;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ServerName();

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?")]
    private static partial Regex VersionText();
}
