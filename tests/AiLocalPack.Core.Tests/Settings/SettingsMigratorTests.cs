using System.Text.Json.Nodes;
using AiLocalPack.Core.Settings;

namespace AiLocalPack.Core.Tests.Settings;

public sealed class SettingsMigratorTests
{
    private static SettingsMigrator RenamesUpToVersion3() => new(3, new Dictionary<int, Func<JsonObject, JsonObject>>
    {
        [1] = d =>
        {
            d["b"] = d["a"]?.DeepClone();
            d.Remove("a");
            return d;
        },
        [2] = d =>
        {
            d["c"] = d["b"]?.DeepClone();
            d.Remove("b");
            return d;
        },
    });

    [Fact]
    public void Applies_every_step_in_order_and_stamps_the_current_version()
    {
        // Arrange
        var document = JsonNode.Parse("""{ "schemaVersion": 1, "a": "x" }""")!.AsObject();

        // Act
        var migrated = RenamesUpToVersion3().Migrate(document);

        // Assert
        migrated.ToJsonString().Should().Be("""{"schemaVersion":3,"c":"x"}""");
    }

    [Fact]
    public void Starts_from_the_version_the_document_has()
    {
        // Arrange
        var document = JsonNode.Parse("""{ "schemaVersion": 2, "b": "x" }""")!.AsObject();

        // Act
        var migrated = RenamesUpToVersion3().Migrate(document);

        // Assert
        migrated.ToJsonString().Should().Be("""{"schemaVersion":3,"c":"x"}""");
    }

    [Fact]
    public void A_document_without_a_version_is_version_1()
    {
        // Arrange
        var document = JsonNode.Parse("""{ "a": "x" }""")!.AsObject();

        // Act
        var version = SettingsMigrator.VersionOf(document);

        // Assert
        version.Should().Be(1);
    }

    [Fact]
    public void A_document_from_a_newer_app_is_left_unchanged()
    {
        // Arrange
        var document = JsonNode.Parse("""{ "schemaVersion": 7, "a": "x" }""")!.AsObject();

        // Act
        var migrated = RenamesUpToVersion3().Migrate(document);

        // Assert
        migrated.ToJsonString().Should().Be("""{"schemaVersion":7,"a":"x"}""");
    }

    [Fact]
    public void A_missing_step_is_a_programming_error()
    {
        // Arrange
        var migrator = new SettingsMigrator(2, new Dictionary<int, Func<JsonObject, JsonObject>>());
        var document = JsonNode.Parse("""{ "schemaVersion": 1 }""")!.AsObject();

        // Act
        var act = () => migrator.Migrate(document);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*from schema version 1 to 2*");
    }
}
