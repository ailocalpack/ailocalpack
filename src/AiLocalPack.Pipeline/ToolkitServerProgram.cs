using AiLocalPack.Agents;
using AiLocalPack.Core.Background;
using AiLocalPack.Core.Storage;
using AiLocalPack.Mcp;

namespace AiLocalPack.Pipeline;

/// <summary>
/// How the agent of a stage starts the toolkit MCP server (task 45) for the job: the desktop app in its hidden mode
/// (<see cref="ToolkitServerLaunch.Argument"/>), or the CLI's <c>mcp</c> command. The server gets the app's own
/// <see cref="ForwardedVariables"/>, since an agent doesn't pass every variable on to the servers it starts.
/// </summary>
public sealed class ToolkitServerProgram
{
    private readonly AppLaunch _app;
    private readonly IReadOnlyList<string> _mode;

    private ToolkitServerProgram(AppLaunch app, IReadOnlyList<string> mode, IReadOnlyDictionary<string, string> environment)
    {
        _app = app;
        _mode = mode;
        Environment = environment;
    }

    /// <summary>Variables that change where the app keeps its files and what the toolkit reads (dev, tests, portable).</summary>
    public static IReadOnlyList<string> ForwardedVariables { get; } = ["AILOCALPACK_HOME", "AILOCALPACK_TOOLKIT_ALL_ENGINES"];

    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>The desktop app (<c>&lt;app&gt; --mcp-server …</c>).</summary>
    public static ToolkitServerProgram DesktopApp(AppLaunch app, OsEnvironment env) => new(app, [ToolkitServerLaunch.Argument], Forwarded(env));

    /// <summary>The CLI (<c>ailocalpack mcp …</c>).</summary>
    public static ToolkitServerProgram Cli(AppLaunch cli, OsEnvironment env) => new(cli, ["mcp"], Forwarded(env));

    /// <summary>The server of one job: confined to <paramref name="root"/>, storing into <paramref name="jobFolder"/>.</summary>
    public AgentMcpServer For(string root, string jobFolder, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        return new AgentMcpServer(
            ToolkitServerLaunch.AgentServerName,
            _app.Executable,
            _app.With([.. _mode, "--root", Path.GetFullPath(root), "--job", Path.GetFullPath(jobFolder), "--lang", language]))
        {
            Environment = Environment,
        };
    }

    private static Dictionary<string, string> Forwarded(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ForwardedVariables)
        {
            if (env.GetVariable(name) is { Length: > 0 } value)
            {
                values[name] = value;
            }
        }

        return values;
    }
}
