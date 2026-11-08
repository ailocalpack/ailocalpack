using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;

namespace AiLocalPack.Agents.Tests;

/// <summary>The fake agent program (<c>AiLocalPack.FakeAgent</c>, copied next to the tests) and recordings for it.</summary>
internal static class FakeAgentProgram
{
    /// <summary>Runs the program through the <c>dotnet</c> host that runs the tests.</summary>
    public static FakeAgentReplayer Replayer
    {
        get
        {
            var program = Path.Combine(AppContext.BaseDirectory, "AiLocalPack.FakeAgent.dll");
            if (!File.Exists(program))
            {
                throw new FileNotFoundException("The fake agent wasn't copied next to the tests.", program);
            }

            // dotnet test sets DOTNET_HOST_PATH to the muxer that runs the tests.
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            return new FakeAgentReplayer(dotnet, [program]);
        }
    }

    /// <summary>A recording file of the given lines (events through <see cref="Event"/>, controls through <see cref="Control"/>).</summary>
    public static string Write(TempDir dir, string name, params string[] lines)
    {
        dir.File(name, string.Join('\n', lines) + "\n");
        return dir.Combine(name);
    }

    public static string Event(AgentEvent e) => AgentEventJson.Write(e);

    public static string Control(string name, string? extra = null) =>
        extra is null ? $$"""{"control":"{{name}}"}""" : $$"""{"control":"{{name}}",{{extra}}}""";

    public static string Done(string result = "done") => Event(new AgentDone(AgentOutcome.Succeeded, result));
}
