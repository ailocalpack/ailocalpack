using AiLocalPack.Core.Background;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Mcp;

/// <summary>
/// The toolkit server as the agent starts it: the desktop app runs itself with <see cref="Argument"/> (hidden mode:
/// no window, no single-instance check, only the protocol on standard output), so players need no second program.
/// The CLI's <c>ailocalpack mcp</c> (development and manual checks) takes the same options.
/// </summary>
public static class ToolkitServerLaunch
{
    /// <summary>First argument of the app's hidden server mode.</summary>
    public const string Argument = "--mcp-server";

    /// <summary>Name the agent knows the server by (its tools are <c>mcp__ailocalpack__…</c>).</summary>
    public const string AgentServerName = "ailocalpack";

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] == Argument;
    }

    /// <summary>The command line an agent runs to start the server for one working copy.</summary>
    public static (string Executable, IReadOnlyList<string> Arguments) Command(AppLaunch app, string root, string jobFolder, string? language)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobFolder);
        string[] own = [Argument, "--root", Path.GetFullPath(root), "--job", Path.GetFullPath(jobFolder)];
        return (app.Executable, language is null ? app.With(own) : app.With([.. own, "--lang", language]));
    }

    /// <summary>The options of <c>--mcp-server --root &lt;dir&gt; --job &lt;dir&gt; [--lang &lt;tag&gt;]</c>, or the reason they are wrong.</summary>
    public static (ToolkitServerOptions? Options, string? Error) Parse(IReadOnlyList<string> args, RecipeTools tools)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(tools);
        string? root = null, job = null, language = null;
        for (var i = IsRequested(args) ? 1 : 0; i < args.Count; i++)
        {
            if (i + 1 >= args.Count)
            {
                return (null, $"{args[i]} needs a value.");
            }

            switch (args[i])
            {
                case "--root": root = args[++i]; break;
                case "--job": job = args[++i]; break;
                case "--lang": language = args[++i]; break;
                default: return (null, $"Unknown option '{args[i]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(job))
        {
            return (null, "--root <working copy> and --job <job folder> are required.");
        }

        return language is not null && !GameLanguages.Tags.Contains(language, StringComparer.Ordinal)
            ? (null, $"'{language}' is not a supported language tag (e.g. en, ru, pt-BR).")
            : (new ToolkitServerOptions(root, job, language, tools), null);
    }

    /// <summary>Serves until standard input closes; a refusal to start goes to <paramref name="error"/> with exit code 1.</summary>
    public static async Task<int> RunAsync(ToolkitServerOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(error);
        ToolkitSession session;
        try
        {
            session = new ToolkitSession(options);
        }
        catch (ArgumentException e)
        {
            await error.WriteLineAsync(e.Message.Split(" (Parameter", 2)[0]).ConfigureAwait(false);
            return 1;
        }

        try
        {
            await ToolkitMcpServer.RunStdioAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>The desktop app's hidden mode: parses <paramref name="args"/> and serves (blocks).</summary>
    public static int Run(IReadOnlyList<string> args, OsEnvironment env, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (options, problem) = Parse(args, RecipeTools.Create(env));
        if (options is null)
        {
            error.WriteLine(problem);
            return 1;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        return RunAsync(options, error, stop.Token).GetAwaiter().GetResult();
    }
}
