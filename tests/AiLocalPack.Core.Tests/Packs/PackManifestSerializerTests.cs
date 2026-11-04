using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Packs;

public sealed class PackManifestSerializerTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    public static TheoryData<string> ValidFixtures => [.. PackFixtures.ValidNames()];

    public static TheoryData<string> InvalidFixtures => [.. PackFixtures.InvalidNames()];

    /// <summary>Invalid sample → the error it must produce, and whether the JSON Schema can see it too.</summary>
    public static TheoryData<string, string, string, bool> InvalidCases => new()
    {
        { "bad-file-hash", "builds[0].files[\"www/data/System.json\"]", "is not a SHA-256 hash", true },
        { "bad-game-id", "game", "'steam:0620' is not a game ID", true },
        { "bad-pack-id", "id", "'Full_RU' is not a valid ID", true },
        { "bad-recipe-version", "recipe.version", "'1.0' is not a version", true },
        { "bad-status", "status", "'beta' is not a status", true },
        { "coverage-over-total", "coverage.translated", "more than the total of 1520 strings", false },
        { "date-without-time", "created", "is not a date and time with a time zone", true },
        { "duplicate-font", "fonts[1]", "'noto-sans' is listed twice", true },
        { "file-path-escapes", "builds[0].files[\"../save.json\"]", "is not a relative file path", true },
        { "manual-game-with-build-id", "builds[0].buildId", "is only for store games", false },
        { "missing-game", "game", "is missing", true },
        { "newer-schema-version", "schemaVersion", "made for a newer version of the app", true },
        { "no-authors", "authors", "must name at least one author", true },
        { "no-recipe-no-patch", "", "needs a recipe", true },
        { "patch-files-differ", "patch.files", "must list the same files as builds[0].files", false },
        { "patch-over-http", "patch.url", "is not an https:// URL", true },
        { "patch-with-two-builds", "builds", "applies to exactly one game build", false },
        { "recipe-and-patch", "", "either a recipe or a binary patch, not both", true },
        { "recipe-without-strings", "strings", "a recipe pack needs its string table", true },
        { "revision-by-stranger", "revisions[1].by", "'mallory' is not listed in authors", false },
        { "revision-gap", "revisions[1].revision", "is 3, expected 2", false },
        { "revision-mismatch", "revision", "is 3, but the last entry of revisions is 2", false },
        { "revision-not-a-number", "revision", "has the wrong type", true },
        { "steam-build-without-id", "builds[1].buildId", "a Steam game build needs its build ID", false },
        { "unknown-language", "language", "'russian' is not a supported language", true },
        { "updated-before-created", "updated", "is earlier than created", false },
        { "wrong-license", "license", "'MIT' is not allowed", true },
    };

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_sample_passes(string name)
    {
        // Arrange
        var json = PackFixtures.ReadValid(name);

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_sample_passes_the_schema(string name)
    {
        // Arrange
        var json = PackFixtures.ReadValid(name);

        // Act
        var errors = PackFixtures.SchemaErrors(PackFixtures.ManifestSchema, json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_sample_fails_with_the_expected_error(string name, string field, string message, bool schemaRejects)
    {
        // Arrange
        _ = schemaRejects;
        var json = PackFixtures.ReadInvalid(name);

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Should().Contain(e => e.Field == field && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Schema_rejects_the_invalid_samples_it_can_see(string name, string field, string message, bool schemaRejects)
    {
        // Arrange
        _ = (field, message);
        var json = PackFixtures.ReadInvalid(name);

        // Act
        var errors = PackFixtures.SchemaErrors(PackFixtures.ManifestSchema, json);

        // Assert
        errors.Should().HaveCount(c => schemaRejects ? c > 0 : c == 0, "the schema {0} catch '{1}'", schemaRejects ? "should" : "cannot", name);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Every_invalid_sample_has_an_expected_error(string name)
    {
        // Arrange
        var names = InvalidCases.Select(c => c.Data.Item1);

        // Act
        var listed = names.Contains(name);

        // Assert
        listed.Should().BeTrue();
    }

    [Fact]
    public void Invalid_manifest_gives_no_model()
    {
        // Arrange
        var json = PackFixtures.ReadInvalid("bad-status");

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Every_problem_is_listed_not_only_the_first()
    {
        // Arrange
        var json = PackFixtures.ReadValid("recipe-steam")
            .Replace("\"status\": \"alpha\"", "\"status\": \"beta\"", StringComparison.Ordinal)
            .Replace("\"license\": \"CC-BY-SA-4.0\"", "\"license\": \"MIT\"", StringComparison.Ordinal);

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Select(e => e.Field).Should().BeEquivalentTo(["status", "license"]);
    }

    [Fact]
    public void Reads_the_model()
    {
        // Arrange
        var json = PackFixtures.ReadValid("recipe-steam");

        // Act
        var manifest = PackManifestSerializer.Read(json).Value!;

        // Assert
        manifest.Should().BeEquivalentTo(new
        {
            Id = "full",
            Game = GameId.Steam(620),
            Language = "ru",
            Title = "Полный перевод",
            Revision = 2,
            Status = PackStatus.Alpha,
            Author = new PackAuthor("alice", "Alice"),
            Agent = new PackAgent("claude-code", "2.1.3", "claude-opus-5-5"),
            TouchedFiles = new[] { "www/data/Map001.json", "www/data/System.json" },
            Strings = new PackStringsFile(PackStringsFile.DefaultPath, new string('d', 64), 48213),
            Coverage = new PackCoverage(1500, 1520),
            Updated = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
        });
    }

    [Fact]
    public void Unknown_future_fields_are_ignored()
    {
        // Arrange
        var json = PackFixtures.ReadValid("future-fields");

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Value!.Id.Should().Be("future");
    }

    [Fact]
    public void Not_json_is_reported_with_its_position()
    {
        // Arrange
        var json = "{\n  \"id\": \"full\",\n  oops\n}";

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Should().ContainSingle().Which.Message.Should().StartWith("Not valid JSON (line 3");
    }

    [Fact]
    public void Duplicate_property_is_rejected()
    {
        // Arrange
        var json = PackFixtures.ReadValid("recipe-manual-game").Replace("\"id\": \"ru\",", "\"id\": \"ru\", \"id\": \"other\",", StringComparison.Ordinal);

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("listed twice");
    }

    [Fact]
    public void Null_document_is_rejected()
    {
        // Arrange
        const string json = "null";

        // Act
        var result = PackManifestSerializer.Read(json);

        // Assert
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("no manifest");
    }

    [Fact]
    public void Written_model_passes_the_schema()
    {
        // Arrange
        var manifest = SampleManifest();

        // Act
        var json = PackManifestSerializer.Write(manifest);

        // Assert
        PackFixtures.SchemaErrors(PackFixtures.ManifestSchema, json).Should().BeEmpty();
    }

    [Fact]
    public void Written_patch_model_passes_the_schema()
    {
        // Arrange
        var manifest = SampleManifest() with
        {
            Recipe = null,
            Patch = new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/p.vcdiff"), HashB, 10, new Dictionary<string, string> { ["data/a.bin"] = HashA }),
        };

        // Act
        var json = PackManifestSerializer.Write(manifest);

        // Assert
        PackFixtures.SchemaErrors(PackFixtures.ManifestSchema, json).Should().BeEmpty();
    }

    [Fact]
    public void Written_model_reads_back_the_same()
    {
        // Arrange
        var manifest = SampleManifest();

        // Act
        var read = PackManifestSerializer.Read(PackManifestSerializer.Write(manifest)).Value!;

        // Assert
        read.Should().BeEquivalentTo(manifest, o => o.Excluding(m => m.Recipe!.Parameters));
    }

    [Fact]
    public void Written_model_keeps_non_ascii_text_readable()
    {
        // Arrange
        var manifest = SampleManifest() with { Title = "Перевод «полный»" };

        // Act
        var json = PackManifestSerializer.Write(manifest);

        // Assert
        json.Should().Contain("Перевод «полный»");
    }

    [Fact]
    public void Written_dates_are_utc()
    {
        // Arrange
        var manifest = SampleManifest() with { Updated = new DateTimeOffset(2026, 11, 4, 15, 0, 0, TimeSpan.FromHours(3)) };

        // Act
        var json = PackManifestSerializer.Write(manifest);

        // Assert
        json.Should().Contain("\"updated\": \"2026-11-04T12:00:00Z\"");
    }

    private static PackManifest SampleManifest()
    {
        var created = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        return new PackManifest(
            "full",
            GameId.Steam(620),
            "ru",
            "Full translation",
            [new PackAuthor("alice", "Alice"), new PackAuthor("bob")],
            [new PackRevision(1, created, "alice", "First"), new PackRevision(2, created.AddDays(3), "bob")],
            PackStatus.Alpha,
            new PackAgent("codex"),
            [new PackBuild("42", new Dictionary<string, string> { ["data/a.bin"] = HashA })],
            new PackRecipe("rpgmaker", "1.2.3"),
            new PackStringsFile(PackStringsFile.DefaultPath, HashB, 100),
            null,
            [new PackDependency("bepinex", "5.4.23")],
            ["noto-sans"],
            new PackCoverage(9, 10),
            created,
            created.AddDays(3));
    }
}
