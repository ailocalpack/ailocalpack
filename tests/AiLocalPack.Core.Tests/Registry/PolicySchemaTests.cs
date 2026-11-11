using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Tests.Packs;
using Json.Schema;

namespace AiLocalPack.Core.Tests.Registry;

/// <summary>
/// The JSON Schemas of the registry's policy files (<c>schemas/opt-out.schema.json</c>, <c>anti-cheat.schema.json</c>,
/// <c>verified-policy.schema.json</c>) agree with the readers: the sample files and the bundled list pass, files the
/// readers refuse fail (task 65).
/// </summary>
public sealed class PolicySchemaTests
{
    private static readonly Lazy<JsonSchema> OptOut = new(() => Load("opt-out.schema.json"));
    private static readonly Lazy<JsonSchema> AntiCheat = new(() => Load("anti-cheat.schema.json"));
    private static readonly Lazy<JsonSchema> Verified = new(() => Load("verified-policy.schema.json"));

    private static JsonSchema Load(string name) =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));

    private static bool ReaderRefuses(string json)
    {
        try
        {
            AntiCheatPolicy.Parse(json);
            return false;
        }
        catch (AntiCheatPolicyException)
        {
            return true;
        }
    }

    private static string Sample(string name) => File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "policy", name));

    private static string Bundled()
    {
        using var stream = typeof(AntiCheatPolicy).Assembly.GetManifestResourceStream("AiLocalPack.Core.AntiCheat.anti-cheat.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Sample_opt_out_list_passes_the_schema()
    {
        // Arrange
        var json = Sample("opt-out.json");

        // Act
        var errors = PackFixtures.SchemaErrors(OptOut.Value, json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Sample_anti_cheat_policy_passes_the_schema()
    {
        // Arrange
        var json = Sample("anti-cheat.json");

        // Act
        var errors = PackFixtures.SchemaErrors(AntiCheat.Value, json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Bundled_anti_cheat_list_passes_the_schema()
    {
        // Arrange
        var json = Bundled();

        // Act
        var errors = PackFixtures.SchemaErrors(AntiCheat.Value, json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Sample_verified_overrides_pass_the_schema()
    {
        // Arrange
        var json = """
            {"schemaVersion": 1, "packs": [
              {"pack": "steam:620/ru/full", "revision": 3, "status": "verified", "note": "checked by hand"},
              {"pack": "steam:620/pt-BR/komplett", "revision": 1, "status": "alpha"}
            ]}
            """;

        // Act
        var errors = PackFixtures.SchemaErrors(Verified.Value, json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "games": [{"game": "steam:x"}]}""")]
    [InlineData("""{"schemaVersion": 1, "games": [{"note": "no game"}]}""")]
    [InlineData("""{"schemaVersion": 2, "games": []}""")]
    public void Opt_out_list_the_reader_refuses_fails_the_schema(string json)
    {
        // Arrange
        var read = RegistryOptOuts.Read(json).Games;

        // Act
        var errors = PackFixtures.SchemaErrors(OptOut.Value, json);

        // Assert
        (read, errors.Count > 0).Should().Be((null, true));
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "none", "name": "x", "markers": ["a"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "EAC", "name": "x", "markers": ["a"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": " ", "markers": ["a"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": "x", "markers": []}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": "x", "markers": ["../up"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": "x", "markers": ["a\\b"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": "x", "markers": ["*/b.dll"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "antiCheats": [{"id": "eac", "name": "x", "markers": ["a//b"]}]}""")]
    [InlineData("""{"schemaVersion": 1, "games": [{"game": "steam:1"}]}""")]
    [InlineData("""{"schemaVersion": 1, "allowed": [{"game": "steam:01"}]}""")]
    public void Anti_cheat_policy_the_reader_refuses_fails_the_schema(string json)
    {
        // Arrange
        var refused = ReaderRefuses(json);

        // Act
        var errors = PackFixtures.SchemaErrors(AntiCheat.Value, json);

        // Assert
        (refused, errors.Count > 0).Should().Be((true, true));
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru", "revision": 1, "status": "verified"}]}""")]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru/full", "revision": 0, "status": "verified"}]}""")]
    [InlineData("""{"schemaVersion": 1, "packs": [{"pack": "steam:620/ru/full", "revision": 1, "status": "broken"}]}""")]
    public void Verified_overrides_the_reader_refuses_fail_the_schema(string json)
    {
        // Arrange
        var read = VerifiedPolicy.Read(json).Overrides;

        // Act
        var errors = PackFixtures.SchemaErrors(Verified.Value, json);

        // Assert
        (read, errors.Count > 0).Should().Be((null, true));
    }
}
