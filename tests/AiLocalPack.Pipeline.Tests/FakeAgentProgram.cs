using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The fake agent program (<c>AiLocalPack.FakeAgent</c>, copied next to the tests) and recordings for it.</summary>
internal static class FakeAgentProgram
{
    /// <summary>Runs the program through the <c>dotnet</c> host that runs the tests.</summary>
    public static FakeAgentReplayer Replayer
    {
        get
        {
            var program = Path.Combine(AppContext.BaseDirectory, "AiLocalPack.FakeAgent.dll");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            return new FakeAgentReplayer(dotnet, [program]);
        }
    }

    public static FakeAgentAdapter Adapter(string recording, AgentAuthState auth = AgentAuthState.SignedIn) =>
        new(new ProcessRunner(), Replayer, recording, auth);

    /// <summary>A recording file of the given lines.</summary>
    public static string Write(TempDir dir, string name, params string[] lines)
    {
        dir.File(name, string.Join('\n', lines) + "\n");
        return dir.Combine(name);
    }

    public static string Event(AgentEvent e) => AgentEventJson.Write(e);

    public static string Done(string result = "done") => Event(new AgentDone(AgentOutcome.Succeeded, result));

    /// <summary>Writes <paramref name="text"/> at <paramref name="path"/> relative to the working copy (what an MCP tool stores).</summary>
    public static string WriteFile(string path, string text) =>
        $$"""{"control":"write","path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"text":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""";

    /// <summary>Saves <c>glossary.json</c> next to the working copy, as <c>put_glossary</c> would.</summary>
    public static string SaveGlossary(string json) => WriteFile("../glossary.json", json);

    public const string RussianGlossary =
        """{"schemaVersion":1,"language":"ru","tone":"informal, ты","entries":[{"term":"Potion","translation":"Зелье","partOfSpeech":"noun","gender":"neuter"},{"term":"Harold","translation":"Гарольд","note":"the hero"}]}""";
}
