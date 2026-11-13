using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The local pack made from a job (task 51): coverage, what stays untranslated, the manifest.</summary>
public sealed class LocalPackBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 30, 15, 500, TimeSpan.Zero);

    private static readonly TextMarkup Markup = new RpgMakerRecipe().Markup;

    private static readonly SourceStrings Source = new(
        new EngineKind(GameEngine.RpgMaker, "mz"),
        "rpgmaker",
        new Version(1, 0, 0),
        ["data/Items.json"],
        [
            new ExtractedString("a", "Potion", "Item name", null, []),
            new ExtractedString("b", "Heals \\V[1] HP", "Item description", null, ["\\V[1]"]),
            new ExtractedString("c", "HP", "Term", null, []),
            new ExtractedString("d", "Ether", "Item name", null, []),
            new ExtractedString("e", "Hello ", "Message", null, []),
        ]);

    private static readonly Game SteamGame = StageHarness.Game with { Version = GameVersion.FromBuildId("1000") };

    private static BuildTranslations Collect(params (string Key, string Text)[] stored) =>
        LocalPackBuilder.Collect(Source, new StringTable("ru", stored.ToDictionary(s => s.Key, s => s.Text)), Markup);

    private static LocalPackInput Input(Game? game = null, AgentRunInfo? agent = null, string agentId = "claude") => new(
        "claude-code",
        game ?? SteamGame,
        "ru",
        agentId,
        "Claude Code",
        agent ?? new AgentRunInfo("2.1.319", "claude-opus-5-5"),
        null,
        "rpgmaker",
        new Version(1, 0, 0),
        new Dictionary<string, string> { ["data/Items.json"] = new('b', 64), ["data/Actors.json"] = new('a', 64) },
        ["noto-sans"],
        new PackCoverage(3, 5),
        Now);

    [Fact]
    public void Coverage_counts_accepted_translations_of_every_string()
    {
        // Act
        var collected = Collect(("a", "Зелье"), ("b", "Лечит \\V[1] ОЗ"), ("c", "HP"));

        // Assert
        collected.Coverage.Should().Be(new PackCoverage(3, 5));
    }

    [Fact]
    public void Strings_without_a_translation_stay_untranslated()
    {
        // Act
        var collected = Collect(("a", "Зелье"), ("d", ""));

        // Assert
        collected.Untranslated.Select(s => s.Key).Should().Equal("b", "c", "d", "e");
    }

    [Fact]
    public void A_translation_the_validator_refuses_is_left_out()
    {
        // Act
        var collected = Collect(("a", "Зелье"), ("b", "Лечит ОЗ"));

        // Assert
        new { Keys = collected.Translations.Keys.ToList(), Refused = collected.Refused.Select(r => (r.Key, r.Issue.Rule)).ToList() }
            .Should().BeEquivalentTo(new { Keys = new List<string> { "a" }, Refused = new List<(string, string)> { ("b", StringValidator.PlaceholderMissing) } });
    }

    [Fact]
    public void A_refused_translation_counts_as_untranslated()
    {
        // Act
        var collected = Collect(("a", "Зелье"), ("b", "Лечит ОЗ"));

        // Assert
        collected.Coverage.Should().Be(new PackCoverage(1, 5));
    }

    [Fact]
    public void A_translation_kept_as_is_counts_as_translated()
    {
        // Act
        var collected = Collect(("c", "HP"));

        // Assert
        new { collected.KeptAsIs, collected.Coverage.Translated, collected.Warnings.Count }.Should().Be(new { KeptAsIs = 1, Translated = 1, Count = 0 });
    }

    [Fact]
    public void Validator_warnings_are_kept_with_the_original_and_translation()
    {
        // Act
        var collected = Collect(("e", "Привет"));

        // Assert
        collected.Warnings.Should().Equal(new LocalPackWarning("e", StringValidator.Whitespace, collected.Warnings[0].Message, "Hello ", "Привет"));
    }

    [Fact]
    public void Nothing_stored_leaves_every_string_untranslated()
    {
        // Act
        var collected = LocalPackBuilder.Collect(Source, null, Markup);

        // Assert
        collected.Coverage.Should().Be(new PackCoverage(0, 5));
    }

    [Fact]
    public void The_manifest_names_the_steam_build_and_the_original_hashes_of_the_touched_files()
    {
        // Act
        var manifest = LocalPackBuilder.Manifest(Input());

        // Assert
        manifest.Builds.Should().BeEquivalentTo([new PackBuild("1000", new Dictionary<string, string> { ["data/Actors.json"] = new('a', 64), ["data/Items.json"] = new('b', 64) })]);
    }

    [Fact]
    public void A_manual_games_manifest_has_no_build_id()
    {
        // Arrange
        var manual = SteamGame with { Id = GameId.Fingerprint(new string('c', 64)), Source = GameSource.Manual, Version = null };

        // Act
        var manifest = LocalPackBuilder.Manifest(Input(manual));

        // Assert
        manifest.Builds[0].BuildId.Should().BeNull();
    }

    [Fact]
    public void A_steam_game_without_a_build_id_has_no_manifest()
    {
        // Arrange
        var input = Input(StageHarness.Game);

        // Act
        var act = () => LocalPackBuilder.Manifest(input);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_manifest_names_the_agent_by_its_registry_id_with_version_and_model()
    {
        // Act
        var manifest = LocalPackBuilder.Manifest(Input());

        // Assert
        manifest.Agent.Should().Be(new PackAgent("claude-code", "2.1.319", "claude-opus-5-5"));
    }

    [Fact]
    public void The_jobs_model_option_stands_in_when_the_agent_reported_none()
    {
        // Arrange
        var input = Input(agent: new AgentRunInfo("0.40.0", null), agentId: "codex") with { Model = "gpt-5.5" };

        // Act
        var manifest = LocalPackBuilder.Manifest(input);

        // Assert
        manifest.Agent.Should().Be(new PackAgent("codex", "0.40.0", "gpt-5.5"));
    }

    [Fact]
    public void The_jobs_quality_goes_into_the_manifests_agent()
    {
        // Arrange
        var input = Input() with { Quality = GenerationQuality.Thorough };

        // Act
        var manifest = LocalPackBuilder.Manifest(input);

        // Assert
        manifest.Agent!.Quality.Should().Be("thorough");
    }

    [Fact]
    public void The_manifest_is_a_first_alpha_revision_by_the_placeholder_author()
    {
        // Act
        var manifest = LocalPackBuilder.Manifest(Input());

        // Assert
        new { manifest.Status, manifest.Revision, Author = manifest.Author.GitHub, By = manifest.Revisions[0].By }
            .Should().Be(new { Status = PackStatus.Alpha, Revision = 1, Author = LocalPackStore.PlaceholderAuthor, By = LocalPackStore.PlaceholderAuthor });
    }

    [Fact]
    public void The_manifest_has_the_recipe_fonts_coverage_and_no_dependencies()
    {
        // Act
        var manifest = LocalPackBuilder.Manifest(Input());

        // Assert
        new { manifest.Recipe, Fonts = string.Join(",", manifest.Fonts), manifest.Coverage, Dependencies = manifest.Dependencies.Count }
            .Should().Be(new { Recipe = (PackRecipe?)new PackRecipe("rpgmaker", "1.0.0"), Fonts = "noto-sans", Coverage = new PackCoverage(3, 5), Dependencies = 0 });
    }

    [Fact]
    public void The_manifest_dates_are_whole_utc_seconds()
    {
        // Act
        var manifest = LocalPackBuilder.Manifest(Input());

        // Assert
        new { manifest.Created, manifest.Updated }.Should().Be(new { Created = new DateTimeOffset(2026, 11, 9, 12, 30, 15, TimeSpan.Zero), Updated = new DateTimeOffset(2026, 11, 9, 12, 30, 15, TimeSpan.Zero) });
    }

    [Fact]
    public void The_manifest_reads_back_valid_once_it_names_its_string_table()
    {
        // Arrange
        var manifest = LocalPackBuilder.Manifest(Input()) with { Strings = new PackStringsFile(PackStringsFile.DefaultPath, new string('d', 64), 100) };

        // Act
        var read = PackManifestSerializer.Read(PackManifestSerializer.Write(manifest));

        // Assert
        read.Errors.Should().BeEmpty();
    }
}
