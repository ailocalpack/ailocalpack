using AiLocalPack.Agents;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using static AiLocalPack.Pipeline.Tests.FakeAgentProgram;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>
/// An update job (task 56) on the RPG Maker fixture: the base pack "lanterns" rev 3 translates the first 40 of its 64
/// strings, 2 of them from an original the game has changed since, plus a key the game no longer has.
/// </summary>
public sealed class UpdateJobTests : IDisposable
{
    private const int Translated = 40;
    private const int ChangedCount = 2;

    private static readonly Game Game = StageHarness.Game with { Version = GameVersion.FromBuildId("2000") };
    private static readonly GenerationBase BaseRef = new("lanterns", 3, Local: false, "Фонарщики");
    private static readonly GenerationOptions UpdateOptions = new(Base: BaseRef);

    private readonly StageHarness _h = new();
    private readonly FakeHttpHandler _http = new();
    private readonly LocalPackStore _store;
    private readonly IReadOnlyList<ExtractedString> _strings;

    public UpdateJobTests()
    {
        _h.WithRpgMakerCopy();
        _store = new LocalPackStore(AppPaths.UnderHome(_h.Dir.Combine("home")));
        _strings = RecipeTools.Builtin.Find("rpgmaker")!.Extract(_h.Copy, TestContext.Current.CancellationToken).Strings;
    }

    public void Dispose()
    {
        _http.Dispose();
        _h.Dispose();
    }

    private IEnumerable<ExtractedString> Kept => _strings.Take(Translated).Skip(ChangedCount);

    private IEnumerable<ExtractedString> Changed => _strings.Take(ChangedCount);

    private IEnumerable<ExtractedString> Added => _strings.Skip(Translated);

    public static PackManifest BaseManifest(Game game) => new(
        "lanterns",
        game.Id,
        "ru",
        "Фонарщики",
        [new PackAuthor("alice")],
        [
            new PackRevision(1, StageHarness.T0.AddDays(-30), "alice"),
            new PackRevision(2, StageHarness.T0.AddDays(-20), "alice"),
            new PackRevision(3, StageHarness.T0.AddDays(-10), "alice"),
        ],
        PackStatus.Verified,
        new PackAgent("claude-code"),
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        new PackStringsFile(PackStringsFile.DefaultPath, new string('b', 64), 100),
        null,
        [],
        [],
        new PackCoverage(Translated, 64),
        StageHarness.T0.AddDays(-30),
        StageHarness.T0.AddDays(-10));

    /// <summary>The base's table: "OLD …" for the first 40 strings (the first 2 from another original) and a removed key.</summary>
    private StringTable BaseTable()
    {
        var strings = _strings.Take(Translated).ToDictionary(s => s.Key, s => "OLD " + s.Original, StringComparer.Ordinal);
        strings["data/Gone.json:/0/name"] = "Пропавшее";
        var originals = _strings.Take(Translated).ToDictionary(s => s.Key, s => OriginalHash.Of(s.Original), StringComparer.Ordinal);
        foreach (var s in Changed)
        {
            originals[s.Key] = OriginalHash.Of("before the update: " + s.Original);
        }

        originals["data/Gone.json:/0/name"] = OriginalHash.Of("Lost");
        return new StringTable("ru", strings) { Originals = originals };
    }

    private void WriteBase(Glossary? glossary = null) => _h.Files.WriteBase(new UpdateBase(BaseManifest(Game), BaseTable(), glossary));

    private Task<GenerationStepResult> ExtractAsync(GenerationOptions? options = null) =>
        new ExtractStage(RecipeTools.Builtin).RunStepAsync(_h.Context(GenerationStage.Extract, options: options ?? UpdateOptions), TestContext.Current.CancellationToken);

    private Task<GenerationStepResult> GlossaryAsync(string recording, int chunks) =>
        new GlossaryStage(new PipelineAgent(Adapter(Write(_h.Dir, "agent.jsonl", recording)), StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Glossary, options: UpdateOptions, counters: new GenerationCounters(64, 38, chunks, 0)), TestContext.Current.CancellationToken);

    private async Task<GenerationStepResult> BuildAsync()
    {
        var stage = new BuildStage(
            Game,
            new PipelineAgent(ScriptedAgent.Translating(), StageHarness.Server),
            RecipeTools.Builtin,
            new BuildServices(
                new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())),
                _store,
                new FakeTestRunner(TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Game.exe is a Windows program"))));
        return await stage.RunStepAsync(_h.Context(GenerationStage.Build, options: UpdateOptions, counters: new GenerationCounters(64, 64, 1, 1)), TestContext.Current.CancellationToken);
    }

    /// <summary>Extracted with the base, then the added and changed strings stored as "NEW …".</summary>
    private async Task TranslateDeltaAsync()
    {
        WriteBase();
        await ExtractAsync();
        var stored = _h.Files.ReadStrings()!;
        var strings = new Dictionary<string, string>(stored.Strings, StringComparer.Ordinal);
        foreach (var s in Added.Concat(Changed))
        {
            strings[s.Key] = "NEW " + s.Original;
        }

        _h.Files.WriteStrings(new StringTable("ru", strings));
    }

    // ---------- Extract ----------

    [Fact]
    public async Task Extract_counts_the_kept_translations_and_plans_chunks_for_the_rest_only()
    {
        // Arrange
        WriteBase();

        // Act
        var result = await ExtractAsync();

        // Assert
        result.Should().Be(new GenerationStepResult.StageDone(new GenerationCounters(64, Translated - ChangedCount, 1, 0)));
    }

    [Fact]
    public async Task Extract_chunks_hold_the_added_and_changed_strings()
    {
        // Arrange
        WriteBase();

        // Act
        await ExtractAsync();

        // Assert
        ChunkPlanFile.Read(_h.Files)!.SelectMany(c => c.Keys).Should().BeEquivalentTo(Added.Concat(Changed).Select(s => s.Key));
    }

    [Fact]
    public async Task Extract_stores_the_base_translations_of_unchanged_strings_only()
    {
        // Arrange
        WriteBase();

        // Act
        await ExtractAsync();

        // Assert
        _h.Files.ReadStrings()!.Strings.Should().BeEquivalentTo(Kept.ToDictionary(s => s.Key, s => "OLD " + s.Original));
    }

    [Fact]
    public async Task Extract_keeps_what_an_earlier_run_stored()
    {
        // Arrange
        WriteBase();
        var key = Kept.First().Key;
        _h.Files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { [key] = "Исправлено" }));

        // Act
        await ExtractAsync();

        // Assert
        _h.Files.ReadStrings()!.Strings[key].Should().Be("Исправлено");
    }

    [Fact]
    public async Task Extract_logs_what_the_update_changed()
    {
        // Arrange
        WriteBase();

        // Act
        await ExtractAsync();

        // Assert
        _h.Log.Should().Contain("Update of lanterns rev 3: 24 new strings, 2 changed, 1 removed, 38 unchanged (their translations kept).");
    }

    [Fact]
    public async Task Extract_without_the_base_files_fails()
    {
        // Arrange: an update job whose base/ folder is gone.

        // Act
        var result = await ExtractAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Disk);
    }

    [Fact]
    public async Task Extract_refuses_a_base_in_another_language()
    {
        // Arrange
        _h.Files.WriteBase(new UpdateBase(BaseManifest(Game) with { Language = "de" }, BaseTable() with { Language = "de" }));

        // Act
        var result = await ExtractAsync();

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Kind.Should().Be(GenerationFailureKind.Unexpected);
    }

    // ---------- Glossary ----------

    [Fact]
    public async Task Glossary_with_nothing_to_translate_is_done_without_the_agent()
    {
        // Arrange
        WriteBase();
        await ExtractAsync();

        // Act
        var result = await GlossaryAsync(Done("ran"), chunks: 0);

        // Assert
        new { result, Ran = File.Exists(Path.Combine(_h.Folder, "logs", "agent-glossary.jsonl")) }
            .Should().BeEquivalentTo(new { result = (GenerationStepResult)new GenerationStepResult.StageDone(), Ran = false });
    }

    [Fact]
    public async Task Glossary_of_a_local_base_is_merged_without_the_agent()
    {
        // Arrange
        WriteBase(new Glossary("ru", "informal", [new GlossaryEntry("Harold", "Гарольд"), new GlossaryEntry("Potion", "Зелье")]));
        await ExtractAsync();
        _h.Files.WriteGlossary(new Glossary("ru", null, [new GlossaryEntry("Harold", "Харальд", Edited: true)]));

        // Act
        var result = await GlossaryAsync(Done("ran"), chunks: 1);

        // Assert
        var glossary = _h.Files.ReadGlossary()!;
        new { result, glossary.Tone, Entries = glossary.Entries.Select(e => $"{e.Term}={e.Translation}").ToList() }
            .Should().BeEquivalentTo(new
            {
                result = (GenerationStepResult)new GenerationStepResult.StageDone(),
                Tone = "informal",
                Entries = new[] { "Harold=Харальд", "Potion=Зелье" },
            });
    }

    [Fact]
    public async Task Glossary_without_a_kept_glossary_asks_the_agent_with_the_update_prompt()
    {
        // Arrange
        WriteBase();
        await ExtractAsync();
        var recording = Write(_h.Dir, "agent.jsonl", """{"control":"prompt"}""", SaveGlossary("""{"schemaVersion":1,"language":"ru","tone":"informal","entries":[{"term":"Harold","translation":"Гарольд"}]}"""), Done());

        // Act
        await new GlossaryStage(new PipelineAgent(Adapter(recording), StageHarness.Server), "Sample Quest")
            .RunStepAsync(_h.Context(GenerationStage.Glossary, options: UpdateOptions, counters: new GenerationCounters(64, 38, 1, 0)), TestContext.Current.CancellationToken);

        // Assert
        var transcript = File.ReadAllLines(Path.Combine(_h.Folder, "logs", "agent-glossary.jsonl"));
        var message = (AgentMessage)AgentEventJson.Read(transcript[0], out _)!;
        message.Text.Should().StartWith("Build the glossary for updating the translation of the video game \"Sample Quest\" (RPG Maker MZ) into Russian.")
            .And.Contain("Its translation \"Фонарщики\" already covers most of it: 38 of the 64 translatable strings");
    }

    // ---------- Build ----------

    [Fact]
    public async Task Build_saves_the_next_revision_of_the_base_pack()
    {
        // Arrange
        await TranslateDeltaAsync();

        // Act
        await BuildAsync();

        // Assert
        var manifest = _store.List().Single().Manifest;
        new
        {
            manifest.Id,
            manifest.Title,
            manifest.Revision,
            Authors = manifest.Authors.Select(a => a.GitHub).ToList(),
            Last = manifest.Revisions[^1],
            manifest.Created,
            manifest.Coverage,
            manifest.Status,
        }.Should().BeEquivalentTo(new
        {
            Id = "lanterns",
            Title = "Фонарщики",
            Revision = 4,
            Authors = new[] { "alice", LocalPackStore.PlaceholderAuthor },
            Last = new PackRevision(4, StageHarness.T0, LocalPackStore.PlaceholderAuthor, "Update for build 2000: 24 new strings, 2 changed, 1 removed."),
            Created = StageHarness.T0.AddDays(-30),
            Coverage = new PackCoverage(64, 64),
            Status = PackStatus.Alpha,
        });
    }

    [Fact]
    public async Task Build_merges_the_kept_and_new_translations_without_the_removed_key()
    {
        // Arrange
        await TranslateDeltaAsync();

        // Act
        await BuildAsync();

        // Assert
        var table = ReadTable(_store.List().Single());
        var expected = Kept.ToDictionary(s => s.Key, s => "OLD " + s.Original);
        foreach (var s in Added.Concat(Changed))
        {
            expected[s.Key] = "NEW " + s.Original;
        }

        table.Strings.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Build_stores_the_hash_of_every_original()
    {
        // Arrange
        await TranslateDeltaAsync();

        // Act
        await BuildAsync();

        // Assert
        ReadTable(_store.List().Single()).Originals.Should().BeEquivalentTo(_strings.ToDictionary(s => s.Key, s => OriginalHash.Of(s.Original)));
    }

    [Fact]
    public async Task Build_reports_what_the_update_changed()
    {
        // Arrange
        await TranslateDeltaAsync();

        // Act
        await BuildAsync();

        // Assert
        _store.List().Single().Report.Update.Should().Be(new LocalPackUpdate(3, 24, 2, 1));
    }

    [Fact]
    public async Task Build_replaces_a_local_base_with_the_same_id()
    {
        // Arrange
        await TranslateDeltaAsync();
        var report = new LocalPackReport("old-job", StageHarness.T0, TestRunResult.Skipped(TestRunSkip.TurnedOff, "off"), LocalPackFonts.NotChecked, [], [], 0);
        _store.Save(BaseManifest(Game), BaseTable(), report);

        // Act
        await BuildAsync();

        // Assert
        _store.List().Select(p => $"{p.Manifest.Id} rev {p.Manifest.Revision}").Should().Equal("lanterns rev 4");
    }

    [Fact]
    public async Task Build_keeps_the_job_glossary_with_the_pack()
    {
        // Arrange
        await TranslateDeltaAsync();
        _h.Files.WriteGlossary(new Glossary("ru", "informal", [new GlossaryEntry("Harold", "Гарольд")]));

        // Act
        await BuildAsync();

        // Assert
        _store.ReadGlossary(Game.Id, "ru", "lanterns")!.Entries.Select(e => e.Translation).Should().Equal("Гарольд");
    }

    [Fact]
    public async Task Build_saves_a_pack_that_passes_the_registry_checks()
    {
        // Arrange
        await TranslateDeltaAsync();

        // Act
        await BuildAsync();

        // Assert
        PackValidator.Validate(_store.List().Single().Folder, TestContext.Current.CancellationToken).Problems.Should().BeEmpty();
    }

    private static StringTable ReadTable(LocalPack pack)
    {
        using var stream = File.OpenRead(Path.Combine(pack.Folder, "strings", "strings.json.gz"));
        return StringTableSerializer.Read(stream).Value!;
    }
}
