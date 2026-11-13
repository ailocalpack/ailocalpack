using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Agents;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>gen run / resume / status / cancel</c> with the fake agent (tasks 47, 50, 51).</summary>
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

    /// <summary>
    /// Saves a glossary and a translation of every string, as the real agent would through <c>put_glossary</c> and
    /// <c>put_translations</c>, then succeeds (so the first chunk stores them all and the later ones find them done).
    /// </summary>
    private string Success
    {
        get
        {
            var strings = JsonSerializer.Serialize(Translations());
            _dir.File(
                "ok.jsonl",
                GlossaryLine
                + $$"""{"control":"write","path":"../strings.json","text":{{strings}}}""" + "\n"
                + AgentEventJson.Write(new AgentDone(AgentOutcome.Succeeded, "ok")) + "\n");
            return _dir.Combine("ok.jsonl");
        }
    }

    /// <summary>The fake agent's control line saving a glossary, as <c>put_glossary</c> would.</summary>
    private static string GlossaryLine =>
        $$"""{"control":"write","path":"../glossary.json","text":{{JsonSerializer.Serialize("""{"schemaVersion":1,"language":"ru","tone":"informal","entries":[{"term":"Potion","translation":"Зелье"}]}""")}}}""" + "\n";

    /// <summary>The fixture's strings translated as <c>RU &lt;original&gt;</c>, as <c>strings.json</c> holds them.</summary>
    private string Translations()
    {
        var game = _dir.Combine("Steam/steamapps/common/Sample Quest");
        var strings = RecipeTools.Builtin.Find("rpgmaker")!.Extract(game).Strings.ToDictionary(s => s.Key, s => "RU " + s.Original);
        using var stream = new MemoryStream();
        StringTableSerializer.Write(new StringTable("ru", strings), stream, compress: false);
        return Encoding.UTF8.GetString(stream.ToArray());
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
        new { result.ExitCode, Completed = result.StdOut.Contains("100% completed, 64 of 64 strings", StringComparison.Ordinal) }.Should().Be(new { ExitCode = 0, Completed = true });
    }

    private string LocalPack => Path.Combine(Home, "data", "local-packs", "games", "steam-620", "ru", "fake");

    [Fact]
    public async Task A_completed_job_prints_its_local_pack()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru");

        // Assert
        result.StdOut.Should().Contain($"Local pack fake: {LocalPack}").And.Contain("coverage: 64 of 64 strings");
    }

    [Fact]
    public async Task The_local_pack_of_a_completed_job_passes_pack_validate()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru");

        // Act
        var result = await RunAsync("pack", "validate", LocalPack);

        // Assert
        new { result.ExitCode, Valid = result.StdOut.Contains("valid (steam:620/ru/fake rev 1, 64 of 64 strings translated)", StringComparison.Ordinal) }
            .Should().Be(new { ExitCode = 0, Valid = true });
    }

    [Fact]
    public async Task Pack_local_lists_the_generated_pack()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru");

        // Act
        var result = await RunAsync("pack", "local");

        // Assert
        result.StdOut.Should().StartWith("steam:620 ru fake (job ");
    }

    [Fact]
    public async Task Pack_local_without_packs_says_so()
    {
        // Act
        var result = await RunAsync("pack", "local");

        // Assert
        result.StdOut.Trim().Should().Be("No local packs.");
    }

    [Fact]
    public async Task After_a_chunk_the_strings_just_translated_are_printed()
    {
        // Arrange
        var glossary = Recording("glossary.jsonl", new AgentDone(AgentOutcome.Succeeded, "ok"));
        File.WriteAllText(glossary, GlossaryLine + File.ReadAllText(glossary));
        await RunAsync("gen", "run", "steam:620", "--fake-agent", glossary, "--lang", "ru", "--until", "glossary");

        // Act
        var result = await RunAsync("gen", "resume", "steam:620", "--fake-agent", Success);

        // Assert
        result.StdOut.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(" → RU ", StringComparison.Ordinal)).Should().Be(3);
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
    public async Task Resume_without_a_job_says_so()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "resume", "steam:620", "--fake-agent", recording);

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "Sample Quest has no open generation job: start one with 'gen run'.\n" });
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
        new { result.ExitCode, Completed = result.StdOut.Contains("100% completed, 64 of 64 strings", StringComparison.Ordinal) }.Should().Be(new { ExitCode = 0, Completed = true });
    }

    [Fact]
    public async Task Workers_out_of_the_range_are_refused()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru", "--workers", "9");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "--workers takes 1 to 8, not 9.\n" });
    }

    [Fact]
    public async Task An_unknown_until_stage_is_refused()
    {
        // Arrange
        var recording = Success;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru", "--until", "build");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "--until takes setup, extract, study, glossary or translate, not 'build'.\n" });
    }

    /// <summary><see cref="Success"/> whose every reply is a game brief (the Study run's answer).</summary>
    private string ThoroughSuccess
    {
        get
        {
            var strings = JsonSerializer.Serialize(Translations());
            var brief = "## Setting\n" + new string('x', 300) + "\n## Characters\nHarold: the hero.";
            _dir.File(
                "thorough.jsonl",
                GlossaryLine
                + $$"""{"control":"write","path":"../strings.json","text":{{strings}}}""" + "\n"
                + AgentEventJson.Write(new AgentDone(AgentOutcome.Succeeded, brief)) + "\n");
            return _dir.Combine("thorough.jsonl");
        }
    }

    [Fact]
    public async Task A_thorough_job_studies_the_game_and_records_its_quality()
    {
        // Arrange
        var recording = ThoroughSuccess;

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", recording, "--lang", "ru", "--quality", "thorough");

        // Assert
        var manifest = File.ReadAllText(Path.Combine(LocalPack, "manifest.json"));
        new
        {
            result.ExitCode,
            Brief = File.Exists(Path.Combine(JobFolder, "brief.md")),
            Quality = manifest.Contains("\"quality\": \"thorough\"", StringComparison.Ordinal),
        }.Should().Be(new { ExitCode = 0, Brief = true, Quality = true });
    }

    [Fact]
    public async Task An_unknown_quality_is_refused()
    {
        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru", "--quality", "best");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "--quality takes quick, balanced or thorough, not 'best'.\n" });
    }

    [Fact]
    public async Task Web_lookup_needs_the_thorough_quality()
    {
        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru", "--web");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "--web works only with --quality thorough.\n" });
    }

    [Fact]
    public async Task The_estimate_names_its_quality()
    {
        // Act
        var result = await RunAsync("gen", "estimate", "steam:620", "--quality", "quick");

        // Assert
        result.StdOut.Should().MatchRegex(@"agent time: .+ \(quick\)");
    }

    [Fact]
    public async Task An_unknown_agent_is_refused()
    {
        // Arrange
        var agent = "gpt";

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--agent", agent, "--lang", "ru");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "'gpt' is not an agent: use claude or codex.\n" });
    }

    [Fact]
    public async Task Resuming_a_fake_agent_job_needs_its_recording()
    {
        // Arrange
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Limit, "--lang", "ru");

        // Act
        var result = await RunAsync("gen", "resume", "steam:620");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().Be(new { ExitCode = 1, StdErr = "The job runs with the fake agent: give its recording with --fake-agent.\n" });
    }

    /// <summary>
    /// The first job's local pack recorded as installed (as the app's install does), then a game update that changes the
    /// potion's description.
    /// </summary>
    private async Task InstallAndUpdateGameAsync()
    {
        await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--lang", "ru");
        var game = _steam.Root + "/steamapps/common/Sample Quest";
        new Core.Install.InstalledPackStore(Core.Storage.AppPaths.UnderHome(Home)).Put(
            new Core.Install.InstalledPack(Core.Games.GameId.Steam(620), "ru", "fake", 1, DateTimeOffset.UtcNow, game, "backup", [], []) { Local = true });
        var items = Path.Combine(game, "data", "Items.json");
        File.WriteAllText(items, File.ReadAllText(items).Replace("Restores 500 HP to one ally.", "Restores 800 HP to one ally.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_translates_the_changed_string_on_top_of_the_installed_pack()
    {
        // Arrange
        await InstallAndUpdateGameAsync();

        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--update");

        // Assert
        result.StdOut.Should().Contain("Update of fake rev 1: 0 new strings, 1 changed, 0 removed, 63 kept.")
            .And.Contain("revision: 2 (update of rev 1: 0 new strings, 1 changed, 0 removed)");
    }

    [Fact]
    public async Task An_update_without_an_installed_pack_is_refused()
    {
        // Act
        var result = await RunAsync("gen", "run", "steam:620", "--fake-agent", Success, "--update");

        // Assert
        new { result.ExitCode, result.StdErr }.Should().BeEquivalentTo(new { ExitCode = 1, StdErr = "Sample Quest has no installed pack to update.\n" });
    }
}
