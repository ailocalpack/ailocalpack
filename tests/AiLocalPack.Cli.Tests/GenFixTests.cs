using System.Text;
using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>gen fix</c> and <c>gen drafts</c> with the fake agent (task 75).</summary>
public sealed class GenFixTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public GenFixTests()
    {
        _steam = new SteamFixture(_dir);
        _steam.AddGame(620, "Sample Quest");
        _dir.RpgMakerGame("Steam/steamapps/common/Sample Quest");
    }

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private string ReportPath => Path.Combine(Home, "data", "local-packs", "reports", "steam-620", "ru", "fake.json");

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = Home,
            },
            args);

    private Dictionary<string, string> Originals()
    {
        var game = _dir.Combine("Steam/steamapps/common/Sample Quest");
        return RecipeTools.Builtin.Find("rpgmaker")!.Extract(game).Strings.ToDictionary(s => s.Key, s => s.Original);
    }

    /// <summary>A recording that writes <c>strings.json</c> with the given texts (key → text) and replies.</summary>
    private string Recording(string name, Func<string, string, string> translate, string reply, bool glossary = false)
    {
        var strings = Originals().ToDictionary(e => e.Key, e => translate(e.Key, e.Value));
        using var stream = new MemoryStream();
        StringTableSerializer.Write(new StringTable("ru", strings), stream, compress: false);
        var text = JsonSerializer.Serialize(Encoding.UTF8.GetString(stream.ToArray()));
        _dir.File(name, (glossary ? GlossaryLine : string.Empty)
            + $$"""{"control":"write","path":"../strings.json","text":{{text}}}""" + "\n"
            + AgentEventJson.Write(new AgentToolCall("1", "mcp__ailocalpack__put_translations", "{}")) + "\n"
            + AgentEventJson.Write(new AgentDone(AgentOutcome.Succeeded, reply)) + "\n");
        return _dir.Combine(name);
    }

    private static string GlossaryLine =>
        $$"""{"control":"write","path":"../glossary.json","text":{{JsonSerializer.Serialize("""{"schemaVersion":1,"language":"ru","tone":"informal","entries":[{"term":"Potion","translation":"Зелье"}]}""")}}}""" + "\n";

    private string Generate => Recording("generate.jsonl", (_, o) => "RU " + o, "ok", glossary: true);

    /// <summary>Changes the first <paramref name="count"/> strings (in key order).</summary>
    private string Fix(int count, string name = "fix.jsonl")
    {
        var first = Originals().Keys.Order(StringComparer.Ordinal).Take(count).ToHashSet(StringComparer.Ordinal);
        return Recording(name, (k, o) => first.Contains(k) ? "ИСПР " + o : "RU " + o, "Fixed the separators.");
    }

    private string Answer => Recording("answer.jsonl", (_, o) => "RU " + o, "Which shop?\nChoices: The first | The second");

    private async Task GeneratedAsync()
    {
        var run = await RunAsync("gen", "run", "steam:620", "--fake-agent", Generate, "--lang", "ru");
        run.ExitCode.Should().Be(0, run.StdErr);
    }

    [Fact]
    public async Task A_fix_that_changes_a_few_strings_builds_draft_2()
    {
        // Arrange
        await GeneratedAsync();

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "The game crashes after the logo.", "--fake-agent", Fix(3));

        // Assert
        new { result.ExitCode, Draft = result.StdOut.Contains("Draft 2: 3 strings changed, 0 glossary entries, test run", StringComparison.Ordinal) }
            .Should().Be(new { ExitCode = 0, Draft = true });
    }

    [Fact]
    public async Task The_new_draft_is_the_local_pack_with_its_number()
    {
        // Arrange
        await GeneratedAsync();

        // Act
        await RunAsync("gen", "fix", "steam:620", "Fix it.", "--fake-agent", Fix(3));

        // Assert
        File.ReadAllText(ReportPath).Should().Contain("\"draft\": 2");
    }

    [Fact]
    public async Task A_fix_of_more_than_50_strings_leaves_a_plan_and_changes_nothing()
    {
        // Arrange
        await GeneratedAsync();
        var before = File.ReadAllText(ReportPath);

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "Use formal address everywhere.", "--fake-agent", Fix(60));

        // Assert
        new { result.ExitCode, Plan = result.StdOut.Contains("Plan (waiting): 60 strings", StringComparison.Ordinal), Report = File.ReadAllText(ReportPath) }
            .Should().Be(new { ExitCode = 0, Plan = true, Report = before });
    }

    [Fact]
    public async Task Approving_the_plan_builds_the_next_draft()
    {
        // Arrange
        await GeneratedAsync();
        var recording = Fix(60);
        await RunAsync("gen", "fix", "steam:620", "Use formal address everywhere.", "--fake-agent", recording);

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "--approve", "--fake-agent", recording);

        // Assert
        new { result.ExitCode, Draft = result.StdOut.Contains("Draft 2: 60 strings changed", StringComparison.Ordinal) }.Should().Be(new { ExitCode = 0, Draft = true });
    }

    [Fact]
    public async Task Dropping_the_plan_changes_nothing()
    {
        // Arrange
        await GeneratedAsync();
        await RunAsync("gen", "fix", "steam:620", "Use formal address everywhere.", "--fake-agent", Fix(60));

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "--drop");

        // Assert
        new { result.ExitCode, result.StdOut }.Should().Be(new { ExitCode = 0, StdOut = "Plan dropped: nothing changed.\n" });
    }

    [Fact]
    public async Task An_answer_without_changes_prints_the_reply_and_its_choices()
    {
        // Arrange
        await GeneratedAsync();

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "The shopkeeper is rude.", "--fake-agent", Answer);

        // Assert
        result.StdOut.Should().Contain("Which shop?\n  choices: The first | The second\n");
    }

    [Fact]
    public async Task Going_back_makes_draft_1_current_again()
    {
        // Arrange
        await GeneratedAsync();
        await RunAsync("gen", "fix", "steam:620", "Fix it.", "--fake-agent", Fix(3));

        // Act
        var result = await RunAsync("gen", "drafts", "steam:620", "--back", "1");

        // Assert
        new { result.ExitCode, Draft = File.ReadAllText(ReportPath).Contains("\"draft\"", StringComparison.Ordinal) }.Should().Be(new { ExitCode = 0, Draft = false });
    }

    [Fact]
    public async Task Drafts_lists_each_draft_and_the_conversation()
    {
        // Arrange
        await GeneratedAsync();
        await RunAsync("gen", "fix", "steam:620", "Fix it.", "--fake-agent", Fix(3));

        // Act
        var result = await RunAsync("gen", "drafts", "steam:620");

        // Assert
        result.StdOut.Should().Contain("Draft 1: test run").And.Contain("Draft 2 (current): test run").And.Contain("> Fix it.");
    }

    [Fact]
    public async Task A_fix_needs_a_finished_translation()
    {
        // Arrange
        var home = Home;

        // Act
        var result = await RunAsync("gen", "fix", "steam:620", "Fix it.", "--fake-agent", Fix(3));

        // Assert
        new { result.ExitCode, result.StdErr, Home = home }.Should().Be(new
        {
            ExitCode = 1,
            StdErr = "Sample Quest has no finished translation to fix (its job folder or local pack is gone): make one with 'gen run'.\n",
            Home = home,
        });
    }
}
