using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Prompts as versioned resources, rendered and snapshot-tested (tasks 49, 50).</summary>
public sealed class PromptTemplateTests
{
    private static Dictionary<string, string> GlossaryValues =>
        PromptValues.Glossary("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12);

    [Fact]
    public void The_glossary_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = GlossaryValues;

        // Act
        var prompt = PromptTemplate.Glossary.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("glossary.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_glossary_update_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.GlossaryUpdate("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, "Фонарщики", 1_180);

        // Act
        var prompt = PromptTemplate.GlossaryUpdate.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("glossary-update.v1.ru.txt", prompt));
    }

    private static Dictionary<string, string> TranslateValues(bool retry)
    {
        var source = new SourceStrings(new EngineKind(GameEngine.RpgMaker, "mz"), "rpgmaker", new Version(1, 0, 0), ["data/Map001.json"], []);
        Toolkit.Recipes.ExtractedString[] strings =
        [
            new("data/Map001.json:/events/1/pages/0/list/1/parameters/0", "\\N[1], drink this Potion.", "Line said by Harold", null, ["\\N[1]"]),
            new("data/Map001.json:/events/1/pages/0/list/2/parameters/0", "Thanks!", "Line said by Mira", 20, []),
        ];
        Dictionary<string, string?>? problems = retry
            ? new() { [strings[0].Key] = null, [strings[1].Key] = "the stored translation \"Спасибо огромнейшее тебе!\" is refused: The text is 25 characters long; the limit is 20." }
            : null;
        return TranslateStage.Values(
            "Sample Quest",
            "ru",
            source,
            2,
            7,
            strings,
            [new("Potion", "Зелье", "heals 50 HP", "noun", "neuter")],
            "informal, ты",
            problems);
    }

    [Fact]
    public void The_translate_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = TranslateValues(retry: false);

        // Act
        var prompt = PromptTemplate.Translate.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("translate.v2.ru.txt", prompt));
    }

    [Fact]
    public void The_translate_retry_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = TranslateValues(retry: true);

        // Act
        var prompt = PromptTemplate.TranslateRetry.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("translate-retry.v2.ru.txt", prompt));
    }

    [Fact]
    public void The_study_prompt_with_web_lookup_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.Study("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, webLookup: true);

        // Act
        var prompt = PromptTemplate.Study.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("study.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_study_prompt_without_web_lookup_says_so()
    {
        // Arrange
        var values = PromptValues.Study("Sample Quest", "ru", new EngineKind(GameEngine.RpgMaker, "mz"), 1_234, 12, webLookup: false);

        // Act
        var prompt = PromptTemplate.Study.Render(values);

        // Assert
        prompt.Should().Contain("2. You have no web access: work from the strings alone.");
    }

    [Fact]
    public void The_brief_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.SystemBrief("## Setting\nA flooded megacity.");

        // Act
        var prompt = PromptTemplate.SystemBrief.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system-brief.v1.txt", prompt));
    }

    [Fact]
    public void The_references_system_prompt_of_an_extract_run_matches_its_snapshot()
    {
        // Arrange
        var manifest = new Core.Packs.PackManifest(
            "full", Core.Games.GameId.Steam(620), "en", "Full translation",
            [new Core.Packs.PackAuthor("alice")], [new Core.Packs.PackRevision(3, DateTimeOffset.UnixEpoch, "alice")],
            Core.Packs.PackStatus.Verified, null,
            [new Core.Packs.PackBuild("1000", new Dictionary<string, string> { ["data/Map001.json"] = new('a', 64) })],
            new Core.Packs.PackRecipe("rpgmaker", "1.0.0"), null, null, [], [], new Core.Packs.PackCoverage(1, 1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var references = new ReferenceSet([new ReferencePack(manifest, null, SameBuild: true)]);
        var values = PromptValues.SystemReferences("Sample Quest", "ru", references, references.TouchedFiles);

        // Act
        var prompt = PromptTemplate.SystemReferences.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system-refs.v1.ru.txt", prompt));
    }

    [Fact]
    public void The_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.System("Sample Quest", "pt-BR");

        // Act
        var prompt = PromptTemplate.System.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system.v2.pt-BR.txt", prompt));
    }

    [Fact]
    public void The_scripts_system_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.SystemScripts("Lantern Tale", "ru", "/data/work/fp-ab/job/game");

        // Act
        var prompt = PromptTemplate.SystemScripts.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("system-scripts.v2.ru.txt", prompt));
    }

    [Fact]
    public void The_unknown_engine_extract_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.ExtractUnknown("Lantern Tale", "ru", EngineKind.Unknown, "/data/work/fp-ab/job/game", 5);

        // Act
        var prompt = PromptTemplate.ExtractUnknown.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("extract-unknown.v2.ru.txt", prompt));
    }

    [Fact]
    public void The_unknown_engine_rebuild_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.RebuildUnknown("Lantern Tale", "ru", "/data/work/fp-ab/job/game", 1_234);

        // Act
        var prompt = PromptTemplate.RebuildUnknown.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("rebuild-unknown.v2.ru.txt", prompt));
    }

    [Fact]
    public void A_missing_field_is_refused()
    {
        // Arrange
        var values = GlossaryValues;
        values.Remove("stringCount");

        // Act
        var act = () => PromptTemplate.Glossary.Render(values);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The glossary v1 prompt needs a value for {{stringCount}}.*");
    }

    [Fact]
    public void A_value_the_prompt_has_no_field_for_is_refused()
    {
        // Arrange
        var values = PromptValues.System("Sample Quest", "ru");
        values["tone"] = "formal";

        // Act
        var act = () => PromptTemplate.System.Render(values);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The system v2 prompt has no {{tone}} field.*");
    }

    [Fact]
    public void A_rendered_prompt_keeps_no_field_marks()
    {
        // Arrange
        var values = GlossaryValues;

        // Act
        var prompt = PromptTemplate.Glossary.Render(values);

        // Assert
        prompt.Should().NotContain("{{").And.NotContain("}}");
    }

    [Fact]
    public void A_prompt_is_labelled_with_its_version()
    {
        // Arrange
        var prompt = PromptTemplate.Glossary;

        // Act
        var label = prompt.Label;

        // Assert
        label.Should().Be("glossary v1");
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("de", "German")]
    [InlineData("qx-Zzzz", "qx-Zzzz")]
    public void Languages_are_named_in_English(string tag, string name)
    {
        // Arrange
        var language = tag;

        // Act
        var result = PromptValues.LanguageName(language);

        // Assert
        result.Should().Be(name);
    }

    [Fact]
    public void The_fix_prompt_matches_its_snapshot()
    {
        // Arrange
        var context = new FixContext(1, 64, 64, ["data/Map001.json", "data/Items.json"],
            new Dictionary<string, string> { ["Items/1/name"] = "Fire | Ice" }, new Dictionary<string, string> { ["Items/1/name"] = "Огонь | Лёд" });
        var attachments = FixPrompt.Attachments([FixAttachment.Session(TimeSpan.FromSeconds(4), 3), FixAttachment.Changes(), FixAttachment.ForStrings(["Items/1/name"])], context);
        var history = FixPrompt.History([FixTurn.FromPlayer(1, new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero), "Names are too long.", [])]);
        var values = PromptValues.Fix("Sample Quest", new EngineKind(GameEngine.RpgMaker, "mz"), 64, 1, "The game crashes right after the first loading screen.", attachments, history, scripts: false);

        // Act
        var prompt = PromptTemplate.Fix.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("fix.v4.ru.txt", prompt));
    }

    [Fact]
    public void The_fix_prompt_for_the_agents_own_scripts_matches_its_snapshot()
    {
        // Arrange
        var context = new FixContext(1, 10, 12, [], new Dictionary<string, string>(), new Dictionary<string, string>());
        var values = PromptValues.Fix("Lantern Tale", new EngineKind(GameEngine.Unknown, null), 12, 2, "Squares instead of letters.", FixPrompt.Attachments([], context), FixPrompt.History([]), scripts: true);

        // Act
        var prompt = PromptTemplate.Fix.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("fix.v4.scripts.txt", prompt));
    }

    [Fact]
    public void The_fix_system_prompt_matches_its_snapshot()
    {
        // Act
        var prompt = PromptTemplate.SystemFix.Render(PromptValues.SystemFix("Sample Quest", "ru", null));

        // Assert
        prompt.Should().Be(Snapshot.Text("system-fix.v2.ru.txt", prompt));
    }

    [Fact]
    public void The_fix_system_prompt_for_the_agents_own_scripts_matches_its_snapshot()
    {
        // Act
        var prompt = PromptTemplate.SystemFix.Render(PromptValues.SystemFix("Lantern Tale", "ru", "/work/job/game"));

        // Assert
        prompt.Should().Be(Snapshot.Text("system-fix.v2.scripts.txt", prompt));
    }

    [Fact]
    public void The_repair_prompt_matches_its_snapshot()
    {
        // Arrange
        var values = PromptValues.RepairBuild(
            "Barony",
            new EngineKind(GameEngine.Unknown, null),
            12_215,
            "Test run failed: the game closed with exit code 0xC0000409 (stack buffer overrun / fail-fast) 6.5 s after start. The translated game's output stopped after \"read json file .//data/follower_wheel.json\"; the untranslated game went on with \"read json file .//data/monster_data.json\".",
            "- `lang/en.txt`: 5,840 translations, 812,004 → 1,244,310 bytes; longest translation 1,904 bytes (original 1,020): `lang/en.txt:2501`",
            BuildRepair.ReportCopy,
            BuildRepair.LogCopy,
            BuildRepair.AttemptsText("/no/such/job"),
            BuildRepair.MaxTestRuns,
            scripts: true);

        // Act
        var prompt = PromptTemplate.RepairBuild.Render(values);

        // Assert
        prompt.Should().Be(Snapshot.Text("repair-build.v1.scripts.txt", prompt));
    }

    [Fact]
    public void The_repair_prompt_for_a_toolkit_engine_offers_the_glossary_instead_of_scripts()
    {
        // Arrange
        var values = PromptValues.RepairBuild("Sample Quest", new EngineKind(GameEngine.RpgMaker, "mz"), 64, "Test run failed.", "(none)", "build-failure.json", "test-run.log", "This is the first attempt to fix this crash.", 5, scripts: false);

        // Act
        var prompt = PromptTemplate.RepairBuild.Render(values);

        // Assert
        prompt.Should().Contain("or the glossary (`put_glossary`)");
    }

    [Fact]
    public void The_repair_system_prompt_matches_its_snapshot()
    {
        // Act
        var prompt = PromptTemplate.SystemRepair.Render(PromptValues.SystemRepair("Barony", "ru", "/games/Barony"));

        // Assert
        prompt.Should().Be(Snapshot.Text("system-repair.v1.scripts.txt", prompt));
    }
}
