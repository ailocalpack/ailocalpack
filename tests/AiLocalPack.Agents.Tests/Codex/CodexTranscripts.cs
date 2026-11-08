namespace AiLocalPack.Agents.Tests.Codex;

/// <summary>The recorded Codex JSONL transcripts in <c>tests/fixtures/agents/codex</c> (see its README).</summary>
internal static class CodexTranscripts
{
    public static IReadOnlyList<string> Lines(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "agents", "codex", name + ".jsonl"))
            .Where(l => l.Length > 0)
            .ToList();

    public static ScriptedOutput Output(string name, int exitCode = 0) => ScriptedOutput.Of([.. Lines(name)]) with { ExitCode = exitCode };
}

/// <summary>A fixed clock in a time zone of a given offset.</summary>
internal sealed class ZonedClock(DateTimeOffset now, TimeSpan offset) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test");

    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}
