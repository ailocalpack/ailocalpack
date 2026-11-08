using System.Diagnostics;
using AiLocalPack.Agents.Fake;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary>The fake agent's replay of a recording, in memory (task 40).</summary>
public sealed class FakeAgentReplayTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<(int Code, string Output, string Error)> ReplayAsync(string recording, string input = "")
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        var code = await FakeAgentReplay.RunAsync(
            recording,
            new StringReader(input),
            output,
            error,
            _ => throw new InvalidOperationException("no children here"),
            TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Lines_that_are_not_controls_are_written_as_they_are()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Event(new AgentMessage("hi")), "not json", """{"type":"x"}""");

        // Act
        var (_, output, _) = await ReplayAsync(recording);

        // Assert
        output.Should().Be("{\"type\":\"message\",\"text\":\"hi\"}\nnot json\n{\"type\":\"x\"}\n");
    }

    [Fact]
    public async Task The_prompt_control_writes_the_standard_input_as_a_message()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("prompt"));

        // Act
        var (_, output, _) = await ReplayAsync(recording, "Translate.");

        // Assert
        output.Should().Be(Event(new AgentMessage("Translate.")) + "\n");
    }

    [Fact]
    public async Task The_exit_control_stops_with_its_code()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("exit", "\"code\":7"), "never written");

        // Act
        var (code, output, _) = await ReplayAsync(recording);

        // Assert
        (code, output).Should().Be((7, string.Empty));
    }

    [Fact]
    public async Task The_end_of_the_recording_exits_with_zero()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "x");

        // Act
        var (code, _, _) = await ReplayAsync(recording);

        // Assert
        code.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_recording_exits_with_two()
    {
        // Act
        var (code, _, error) = await ReplayAsync(_dir.Combine("missing.jsonl"));

        // Assert
        (code, error.Contains("missing.jsonl", StringComparison.Ordinal)).Should().Be((2, true));
    }

    [Fact]
    public async Task An_unknown_control_exits_with_two_naming_its_line()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "x", Control("dance"));

        // Act
        var (code, _, error) = await ReplayAsync(recording);

        // Assert
        (code, error).Should().Be((2, $"{recording}:2: unknown control \"dance\"\n"));
    }

    [Fact]
    public async Task A_control_missing_its_value_exits_with_two()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("delay"));

        // Act
        var (code, _, _) = await ReplayAsync(recording);

        // Assert
        code.Should().Be(2);
    }

    [Fact]
    public async Task The_write_control_writes_the_file_with_its_folders()
    {
        // Arrange
        var target = _dir.Combine("job/nested/glossary.json");
        var path = System.Text.Json.JsonSerializer.Serialize(target);
        var text = System.Text.Json.JsonSerializer.Serialize("{\"a\":\"ё\"}");
        var recording = Write(_dir, "r.jsonl", Control("write", $"\"path\":{path},\"text\":{text}"));

        // Act
        await ReplayAsync(recording);

        // Assert
        File.ReadAllText(target).Should().Be("{\"a\":\"ё\"}");
    }

    [Fact]
    public async Task A_write_control_without_text_fails_the_replay()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("write", "\"path\":\"x.json\""));

        // Act
        var (code, _, _) = await ReplayAsync(recording);

        // Assert
        code.Should().Be(2);
    }

    [Fact]
    public void A_replayer_starts_with_its_arguments_then_the_recording()
    {
        // Arrange
        var replayer = new FakeAgentReplayer("/usr/bin/dotnet", ["cli.dll", "agent", "replay"]);

        // Act
        var info = replayer.StartInfo("r.jsonl");

        // Assert
        (info.FileName, info.ArgumentList).Should().BeEquivalentTo(("/usr/bin/dotnet", new[] { "cli.dll", "agent", "replay", "r.jsonl" }));
    }
}
