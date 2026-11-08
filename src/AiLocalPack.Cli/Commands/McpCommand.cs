using System.CommandLine;
using AiLocalPack.Core.Storage;
using AiLocalPack.Mcp;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>mcp --root &lt;working copy&gt; --job &lt;job folder&gt; [--lang &lt;tag&gt;]</c>: the toolkit as an MCP server over
/// standard input / output, for an agent (<see cref="ToolkitMcpServer"/>). Standard output carries only the protocol;
/// a refusal to start goes to standard error with exit code 1. The desktop app serves the same in its hidden mode
/// (<see cref="ToolkitServerLaunch.Argument"/>), which is what agents start; this command is for development and checks.
/// </summary>
internal static class McpCommand
{
    public static Command Build(OsEnvironment env)
    {
        var root = new Option<string>("--root") { Description = "Working copy of the game: the only folder the tools see.", Required = true };
        var job = new Option<string>("--job") { Description = "Job folder for the string store, glossary and rebuild (outside the working copy).", Required = true };
        var lang = new Option<string?>("--lang") { Description = "Language tag of the translation (default: the stored strings' language)." };
        var command = new Command("mcp", "Serve the toolkit to an agent as an MCP server (stdio), confined to one working copy of a game.")
        {
            root, job, lang,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var error = result.InvocationConfiguration.Error;
            List<string> args = ["--root", result.GetValue(root)!, "--job", result.GetValue(job)!];
            if (result.GetValue(lang) is { } language)
            {
                args.AddRange(["--lang", language]);
            }

            var (options, problem) = ToolkitServerLaunch.Parse(args, RecipeTools.Create(env));
            if (options is null)
            {
                await error.WriteLineAsync(problem);
                return 1;
            }

            return await ToolkitServerLaunch.RunAsync(options, error, cancellationToken);
        });
        return command;
    }
}
