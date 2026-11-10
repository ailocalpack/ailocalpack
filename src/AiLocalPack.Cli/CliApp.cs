using System.CommandLine;
using System.CommandLine.Help;
using AiLocalPack.Cli.Commands;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli;

/// <summary>The <c>ailocalpack</c> command tree. Without a command it prints help.</summary>
public static class CliApp
{
    public static RootCommand Build(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var root = new RootCommand("AI Local Pack: install, generate and publish game localizations.")
        {
            GamesCommand.Build(env),
            ToolkitCommand.Build(env),
            IndexCommand.Build(env),
            PackCommand.Build(env),
            AgentCommand.Build(env),
            GitHubCommand.Build(env),
            GenCommand.Build(env),
            McpCommand.Build(env),
            DevCommand.Build(env),
        };
        root.SetAction(result =>
        {
            new HelpAction().Invoke(result);
            return 0;
        });
        return root;
    }
}
