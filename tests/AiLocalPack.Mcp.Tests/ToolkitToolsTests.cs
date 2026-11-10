using System.Text.Json;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Mcp.Tests;

public sealed class ToolkitToolsTests
{
    private const string PotionKey = "data/Items.json:/1/name";
    private const string RareKey = "data/Items.json:/2/description";
    private const int FixtureStrings = 64;

    [Fact]
    public async Task Tool_list_names_every_toolkit_tool()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var tools = await mcp.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "detect_engine", "list_files", "read_file_snippet", "extract_strings", "get_strings", "put_translations",
            "validate_strings", "get_glossary", "put_glossary", "rebuild");
    }

    [Fact]
    public async Task Detect_engine_names_the_engine_and_its_recipe_tool()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("detect_engine");

        // Assert
        (result.GetProperty("engine").GetString(), result.GetProperty("tool").GetString(), result.GetProperty("toolVersion").GetString())
            .Should().Be(("rpgmaker:mz", "rpgmaker", "1.0.0"));
    }

    [Fact]
    public async Task Detect_engine_of_an_unknown_game_says_why_the_toolkit_cant_read_it()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir => dir.File("game/readme.txt", "hello"));

        // Act
        var result = await mcp.ResultAsync("detect_engine");

        // Assert
        result.GetProperty("problem").GetString().Should().Contain("engine is unknown");
    }

    [Fact]
    public async Task List_files_lists_the_top_folder()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("list_files");

        // Assert
        Entries(result).Should().Contain(["data", "js", "fonts", "Game.exe"]);
    }

    [Fact]
    public async Task List_files_filters_by_pattern_recursively()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("list_files", new { pattern = "*.js", recursive = true });

        // Assert
        Entries(result).Should().Equal("js/plugins.js", "js/rmmz_core.js");
    }

    [Fact]
    public async Task List_files_pages_with_next_offset()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("list_files", new { path = "data", limit = 5 });

        // Assert
        (Entries(result).Count, result.GetProperty("nextOffset").GetInt32()).Should().Be((5, 5));
    }

    [Fact]
    public async Task Read_file_snippet_returns_numbered_lines_of_a_text_file()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir => dir.File("game/notes.txt", "one\ntwo\nthree\nfour\n"));

        // Act
        var result = await mcp.ResultAsync("read_file_snippet", new { path = "notes.txt", startLine = 2, lineCount = 2 });

        // Assert
        (result.GetProperty("content").GetString(), result.GetProperty("totalLines").GetInt32(), result.GetProperty("nextLine").GetInt32())
            .Should().Be(("2\ttwo\n3\tthree\n", 4, 4));
    }

    [Fact]
    public async Task Read_file_snippet_dumps_a_binary_file_as_hex()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir => dir.File("game/data.bin", [0x41, 0x00, 0x42]));

        // Act
        var result = await mcp.ResultAsync("read_file_snippet", new { path = "data.bin" });

        // Assert
        result.GetProperty("content").GetString().Should().StartWith("00000000  41 00 42").And.EndWith("|A.B|\n");
    }

    [Fact]
    public async Task Extract_strings_pages_the_extraction()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("extract_strings", new { limit = 10 });

        // Assert
        (result.GetProperty("total").GetInt32(), Keys(result).Count, result.GetProperty("nextOffset").GetInt32())
            .Should().Be((FixtureStrings, 10, 10));
    }

    [Fact]
    public async Task Extract_strings_gives_placeholders_to_keep()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("extract_strings", new { keyPrefix = RareKey });

        // Assert
        result.GetProperty("strings")[0].GetProperty("placeholders").EnumerateArray().Select(p => p.GetString())
            .Should().Equal(@"\C[2]", @"\C[0]");
    }

    [Fact]
    public async Task Extract_strings_filters_by_search()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("extract_strings", new { search = "potion" });

        // Assert
        Keys(result).Should().Equal(PotionKey);
    }

    [Fact]
    public async Task Extract_strings_of_an_unknown_engine_is_a_tool_error()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir => dir.File("game/readme.txt", "hello"));

        // Act
        var error = await mcp.ErrorAsync("extract_strings");

        // Assert
        error.Should().Be("The engine is unknown, so the toolkit can't read its strings. Write your own extractor: it must write strings.json in your workspace.");
    }

    [Fact]
    public async Task Extract_strings_of_an_unknown_engine_reads_the_agents_strings_once_written()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("game/readme.txt", "hello");
            dir.File("job/agent/strings.json", """{"strings": [{"key": "readme.txt:1", "original": "hello", "file": "readme.txt"}]}""");
        });

        // Act
        var result = await mcp.ResultAsync("extract_strings");

        // Assert
        (result.GetProperty("tool").GetString(), Keys(result)).Should().BeEquivalentTo(("agent-script", new[] { "readme.txt:1" }));
    }

    [Fact]
    public async Task Put_translations_of_agent_strings_checks_their_placeholders()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("game/readme.txt", "hello");
            dir.File("job/agent/strings.json", """{"strings": [{"key": "a", "original": "Hi {hero}", "placeholders": ["{hero}"]}]}""");
        });

        // Act
        var result = await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { ["a"] = "Привет" } });

        // Assert
        result.GetProperty("rejected")[0].GetProperty("issues")[0].GetProperty("rule").GetString().Should().Be("placeholder-missing");
    }

    [Fact]
    public async Task Rebuild_of_agent_strings_tells_the_agent_to_run_its_own_script()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("game/readme.txt", "hello");
            dir.File("job/agent/strings.json", """{"strings": [{"key": "a", "original": "hello"}]}""");
            dir.File("job/strings.json", """{"schemaVersion": 1, "language": "ru", "strings": {"a": "привет"}}""");
        });

        // Act
        var error = await mcp.ErrorAsync("rebuild");

        // Assert
        error.Should().Contain("run your own rebuild script");
    }

    [Fact]
    public async Task Put_translations_stores_a_valid_translation_in_the_job_store()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Assert
        Store(mcp).Should().BeEquivalentTo(new { Language = "ru", Strings = new Dictionary<string, string> { [PotionKey] = "Зелье" } });
    }

    [Fact]
    public async Task Put_translations_refuses_one_missing_a_placeholder()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [RareKey] = "Тёплый напиток. Редкость!" } });

        // Assert
        (result.GetProperty("stored").GetInt32(), result.GetProperty("rejected")[0].GetProperty("issues")[0].GetProperty("rule").GetString())
            .Should().Be((0, "placeholder-missing"));
    }

    [Fact]
    public async Task Put_translations_lists_unknown_keys()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { ["no/such:key"] = "x" } });

        // Assert
        result.GetProperty("unknownKeys").EnumerateArray().Select(k => k.GetString()).Should().Equal("no/such:key");
    }

    [Fact]
    public async Task Put_translations_with_an_empty_text_removes_the_stored_one()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Act
        var result = await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "" } });

        // Assert
        (result.GetProperty("removed").GetInt32(), Store(mcp)!.Strings.Count).Should().Be((1, 0));
    }

    [Fact]
    public async Task Put_translations_without_a_target_language_is_a_tool_error()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(language: null);

        // Act
        var error = await mcp.ErrorAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Assert
        error.Should().Be("No target language: the server was started without --lang and nothing is stored yet.");
    }

    [Fact]
    public async Task Get_strings_pages_untranslated_strings_by_default()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Act
        var result = await mcp.ResultAsync("get_strings", new { limit = 200 });

        // Assert
        (result.GetProperty("total").GetInt32(), Keys(result).Contains(PotionKey)).Should().Be((FixtureStrings - 1, false));
    }

    [Fact]
    public async Task Get_strings_by_key_returns_the_stored_translation()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Act
        var result = await mcp.ResultAsync("get_strings", new { keys = new[] { PotionKey, "missing" } });

        // Assert
        (result.GetProperty("strings")[0].GetProperty("translation").GetString(), result.GetProperty("unknownKeys")[0].GetString())
            .Should().Be(("Зелье", "missing"));
    }

    [Fact]
    public async Task Get_strings_with_an_unknown_status_is_a_tool_error()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.CallAsync("get_strings", new { status = "done" });

        // Assert
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_strings_reports_issues_of_texts_without_storing_them()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.ResultAsync("validate_strings", new { translations = new Dictionary<string, string> { [RareKey] = "Напиток \\C[2]" } });

        // Assert
        (result.GetProperty("errors").GetInt32(), Store(mcp)).Should().Be((1, null));
    }

    [Fact]
    public async Task Put_glossary_replaces_a_term_ignoring_case_and_sets_the_tone()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_glossary", new { entries = new[] { new { term = "Potion", translation = "Зелье" } } });

        // Act
        await mcp.ResultAsync("put_glossary", new { entries = new[] { new { term = "potion", translation = "Снадобье" } }, tone = "на ты" });

        // Assert
        new GenerationFiles(mcp.JobDir).ReadGlossary().Should().BeEquivalentTo(
            new Glossary("ru", "на ты", [new GlossaryEntry("potion", "Снадобье")]));
    }

    [Fact]
    public async Task Put_glossary_removes_terms()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_glossary", new { entries = new[] { new { term = "Ash", translation = "Пепел" }, new { term = "Harold", translation = "Гарольд" } } });

        // Act
        var result = await mcp.ResultAsync("put_glossary", new { remove = new[] { "ash" } });

        // Assert
        (result.GetProperty("removed").GetInt32(), result.GetProperty("totalEntries").GetInt32()).Should().Be((1, 1));
    }

    private static readonly Glossary Reviewed = new("ru", null, [
        new GlossaryEntry("Potion", "Эликсир", Edited: true),
        new GlossaryEntry("Ash", "Пепел"),
    ], FormOfAddress.Formal);

    [Fact]
    public async Task Put_glossary_keeps_a_term_the_player_edited()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        new GenerationFiles(mcp.JobDir).WriteGlossary(Reviewed);

        // Act
        var result = await mcp.ResultAsync("put_glossary", new { entries = new[] { new { term = "potion", translation = "Зелье" } } });

        // Assert
        (result.GetProperty("keptByPlayer").EnumerateArray().Single().GetString(), new GenerationFiles(mcp.JobDir).ReadGlossary()!.Entries[0].Translation)
            .Should().Be(("Potion", "Эликсир"));
    }

    [Fact]
    public async Task Put_glossary_doesnt_remove_a_term_the_player_edited()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        new GenerationFiles(mcp.JobDir).WriteGlossary(Reviewed);

        // Act
        await mcp.ResultAsync("put_glossary", new { remove = new[] { "Potion", "Ash" } });

        // Assert
        new GenerationFiles(mcp.JobDir).ReadGlossary()!.Entries.Select(e => e.Term).Should().Equal("Potion");
    }

    [Fact]
    public async Task Put_glossary_keeps_the_players_form_of_address()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        new GenerationFiles(mcp.JobDir).WriteGlossary(Reviewed);

        // Act
        await mcp.ResultAsync("put_glossary", new { tone = "на ты" });

        // Assert
        new GenerationFiles(mcp.JobDir).ReadGlossary()!.FormOfAddress.Should().Be(FormOfAddress.Formal);
    }

    [Fact]
    public async Task Get_glossary_tells_the_players_form_of_address()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        new GenerationFiles(mcp.JobDir).WriteGlossary(Reviewed);

        // Act
        var result = await mcp.ResultAsync("get_glossary", new { });

        // Assert
        result.GetProperty("formOfAddress").GetString().Should().Be("formal");
    }

    [Fact]
    public async Task Put_glossary_refuses_a_term_without_translation()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var error = await mcp.ErrorAsync("put_glossary", new { entries = new[] { new { term = "Ash" } } });

        // Assert
        error.Should().Be("Term 'Ash' has no translation (or set keepAsIs).");
    }

    [Fact]
    public async Task Get_glossary_searches_terms()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_glossary", new { entries = new[] { new { term = "Ash", translation = "Пепел" }, new { term = "Harold", translation = "Гарольд" } } });

        // Act
        var result = await mcp.ResultAsync("get_glossary", new { search = "гар" });

        // Assert
        result.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("term").GetString()).Should().Equal("Harold");
    }

    [Fact]
    public async Task Rebuild_writes_the_changed_files_into_the_job_build_folder()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Act
        var result = await mcp.ResultAsync("rebuild");

        // Assert
        File.ReadAllText(Path.Combine(mcp.JobDir, "build", "data", "Items.json")).Should().Contain("Зелье");
    }

    [Fact]
    public async Task Rebuild_leaves_the_working_copy_untouched()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();
        var before = File.ReadAllText(Path.Combine(mcp.GameDir, "data", "Items.json"));
        await mcp.ResultAsync("put_translations", new { translations = new Dictionary<string, string> { [PotionKey] = "Зелье" } });

        // Act
        await mcp.ResultAsync("rebuild");

        // Assert
        File.ReadAllText(Path.Combine(mcp.GameDir, "data", "Items.json")).Should().Be(before);
    }

    [Fact]
    public async Task Rebuild_without_translations_is_a_tool_error()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var result = await mcp.CallAsync("rebuild");

        // Assert
        result.IsError.Should().BeTrue();
    }

    private static List<string?> Entries(JsonElement result) =>
        [.. result.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("path").GetString())];

    private static List<string?> Keys(JsonElement result) =>
        [.. result.GetProperty("strings").EnumerateArray().Select(e => e.GetProperty("key").GetString())];

    private static StringTable? Store(McpHarness mcp) => new GenerationFiles(mcp.JobDir).ReadStrings();
}
