using System.Text.Json.Nodes;
using AiLocalPack.Agents;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>gen run / resume / status / cancel</c> with the fake agent (task 47).</summary>
public sealed class GenJobTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GenJobTests()
    {
        _steam = new SteamFixture(_dir);
        _steam.AddGame(620, "Sample Quest");
        _dir.RpgMakerGame("Steam/steamapps/common/Sample Quest");
    }

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private string Work => Path.Combine(Home, "data", "work", "steam-620");

    private string JobFolder => Directory.GetDirectories(Work).Single();

    private string JobLog => File.ReadAllText(Path.Combine(JobFolder, "logs", "job.log"));

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = Home,
            },
            args);

    private string Recording(string name, params AgentEvent[] events)
    {
        _dir.File(name, string.Join('\n', events.Select(AgentEventJson.Write)) + "\n");
        return _dir.Combine(name);
    }

    /// <summary>Saves a glossary as the real agent would through <c>put_glossary</c>, then succeeds.</summary>
    private string Success
    {
        get
        {
            var glossary = System.Text.Json.JsonSerializer.Serialize("""{"schemaVersion":1,"language":"ru","tone":"informal","entries":[{"term":"Potion","translation":"Зелье"}]}""");
            _dir.File("ok.jsonl", $$"""{"control":"write","path":"../glossary.json","text":{{glossary}}}""" + "\n" + AgentEventJson.Write(new AgentDone(AgentOutcome.Succeeded, "ok")) + "\n");
            return _dir.Combine("ok.jsonl");
        }
    }

    private string NoGlossary => Recording("no-glossary.jsonl", new AgentDone(AgentOutcome.Succeeded, "ok"));

    private string Limit => Recording(
        "limit.jsonl",
        new AgentLimitReached(DateTimeOffset.UtcNow.AddHours(2), "You hit your limit"),
        new AgentDone(AgentOutcome.LimitReached, Message: "limit"));

    [Fact]
    public async Task A_job_runs_to_completion_with_the_fake_agent()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru");

        // Assert
        new { result.ExitCode, Last = result.StdOut.TrimEnd().Split('\n')[^1].Trim() }.Should().Be(new { ExitCode = 0, Last = "100% completed, 64 of 64 strings" });
    }

    [Fact]
    public async Task The_limit_pauses_the_job_and_status_says_when_it_resumes()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        var result = await RunAsync("gen", "status");

        // Assert
        result.StdOut.Should().Contain("paused at Glossary by the agent's limit, resumes at");
    }

    [Fact]
    public async Task A_second_job_for_the_game_is_refused()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "de");

        // Assert
        new { result.ExitCode, Refused = result.StdErr.Contains("already has a generation job", StringComparison.Ordinal) }
            .Should().Be(new { ExitCode = 1, Refused = true });
    }

    [Fact]
    public async Task Resume_goes_on_from_the_chunk_a_crashed_run_saved()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");
        var jobFile = Path.Combine(JobFolder, "job.json");
        var job = JsonNode.Parse(File.ReadAllText(jobFile))!;
        job["stage"] = "translate";
        job["status"] = "running";
        job.AsObject().Remove("pauseReason");
        job.AsObject().Remove("resumeAt");
        job["counters"]!["chunksDone"] = 1;
        job["counters"]!["translated"] = 50;
        File.WriteAllText(jobFile, job.ToJsonString());

        // Act
        var result = await RunAsync("gen", "resume", "steam:620", "--fake-agent", Success);

        // Assert
        var afterResume = JobLog[JobLog.IndexOf("Resumed at", StringComparison.Ordinal)..];
        new
        {
            result.ExitCode,
            Interrupted = JobLog.Contains("Interrupted at Translate (1 of 2 chunks done)", StringComparison.Ordinal),
            Chunk1Again = afterResume.Contains("chunk 1 of 2", StringComparison.Ordinal),
            Chunk2 = afterResume.Contains("chunk 2 of 2", StringComparison.Ordinal),
            Completed = afterResume.Contains("Completed.", StringComparison.Ordinal),
        }.Should().Be(new { ExitCode = 0, Interrupted = true, Chunk1Again = false, Chunk2 = true, Completed = true });
    }

    [Fact]
    public async Task Resume_without_a_job_says_so()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "resume", "steam:620", "--fake-agent", recording);

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "Sample Quest has no open generation job: start one with 'gen run'." + Environment.NewLine });
    }

    [Fact]
    public async Task Cancel_closes_the_job_and_keeps_its_files()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        var result = await RunAsync("gen", "cancel", "steam:620");

        // Assert
        new { result.ExitCode, Status = (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(JobFolder, "job.json")))!["status"] }
            .Should().Be(new { ExitCode = 0, Status = (string?)"cancelled" });
    }

    [Fact]
    public async Task Cancel_with_delete_removes_the_job_folder()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        await RunAsync("gen", "cancel", "steam:620", "--delete");

        // Assert
        Directory.Exists(Work).Should().BeFalse();
    }

    [Fact]
    public async Task A_game_with_an_anti_cheat_is_refused()
    {
        // Arrange
        _dir.File("Steam/steamapps/common/Sample Quest/EasyAntiCheat/EasyAntiCheat_Setup.exe", "MZ");
        await RunAsync("games", "list");

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru");

        // Assert
        new { result.ExitCode, Refused = result.StdErr.Contains("can't be generated", StringComparison.Ordinal), Work = Directory.Exists(Work) }
            .Should().Be(new { ExitCode = 1, Refused = true, Work = false });
    }

    [Fact]
    public async Task Until_glossary_pauses_the_job_with_the_glossary_saved()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru", "--until", "glossary");

        // Assert
        new
        {
            result.ExitCode,
            Last = result.StdOut.TrimEnd().Split('\n')[^1].Trim(),
            Glossary = File.Exists(Path.Combine(JobFolder, "glossary.json")),
            Source = File.Exists(Path.Combine(JobFolder, "source.json")),
        }.Should().Be(new { ExitCode = 0, Last = "20% paused at Translate (chunk 1 of 2), 0 of 64 strings", Glossary = true, Source = true });
    }

    [Fact]
    public async Task Resume_after_until_goes_on_to_completion()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru", "--until", "glossary");

        // Act
        var result = await RunAsync("gen", "resume", "steam:620", "--fake-agent", Success);

        // Assert
        new { result.ExitCode, Last = result.StdOut.TrimEnd().Split('\n')[^1].Trim() }.Should().Be(new { ExitCode = 0, Last = "100% completed, 64 of 64 strings" });
    }

    [Fact]
    public async Task An_agent_that_saves_no_glossary_fails_the_job()
    {
        // Arrange
        var recording = NoGlossary;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru");

        // Assert
        new { result.ExitCode, Last = result.StdOut.TrimEnd().Split('\n')[^1].Trim() }
            .Should().Be(new { ExitCode = 1, Last = "10% Glossary failed: Fake agent finished without saving a glossary., 0 of 64 strings" });
    }

    [Fact]
    public async Task An_unknown_until_stage_is_refused()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru", "--until", "build");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "--until takes setup, extract, glossary or translate, not 'build'." + Environment.NewLine });
    }

    [Fact]
    public async Task An_unknown_agent_is_refused()
    {
        // Arrange
        var agent = "gpt";

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--agent", agent, "--lang", "ru");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "'gpt' is not an agent: use claude or codex." + Environment.NewLine });
    }

    [Fact]
    public async Task Resuming_a_fake_agent_job_needs_its_recording()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        var result = await RunAsync("gen", "resume", "steam:620");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "The job runs with the fake agent: give its recording with --fake-agent." + Environment.NewLine });
    }
}
