using System.Text.Json.Nodes;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 15, 30, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly TestLogger<SettingsStore> _logger = new();

    private AppPaths Paths => AppPaths.UnderHome(_dir.Path);

    private string SettingsFile => Paths.SettingsFile;

    public void Dispose() => _dir.Dispose();

    private SettingsStore CreateStore(SettingsMigrator? migrator = null) =>
        migrator is null
            ? new SettingsStore(Paths, _logger, new FakeClock(Now))
            : new SettingsStore(SettingsFile, _logger, new FakeClock(Now), migrator);

    private void WriteSettingsFile(string content) => _dir.File("config/settings.json", content);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        // Arrange
        var store = CreateStore();

        // Act
        var settings = store.Load();

        // Assert
        settings.Should().Be(AppSettings.Defaults);
    }

    [Fact]
    public void Missing_file_is_written_out_with_defaults()
    {
        // Arrange
        var store = CreateStore();

        // Act
        store.Load();

        // Assert
        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        json["schemaVersion"]!.GetValue<int>().Should().Be(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        // Arrange
        var first = CreateStore();
        first.Load();
        first.Update(s => s with { UiLanguage = "ru" });

        // Act
        var reloaded = CreateStore().Load();

        // Assert
        reloaded.Should().Be(AppSettings.Defaults with { UiLanguage = "ru" });
    }

    [Fact]
    public void Update_changes_current_and_raises_changed()
    {
        // Arrange
        var store = CreateStore();
        store.Load();
        AppSettings? raised = null;
        store.Changed += (_, s) => raised = s;

        // Act
        store.Update(s => s with { UiLanguage = "ru" });

        // Assert
        store.Current.UiLanguage.Should().Be("ru");
        raised.Should().Be(store.Current);
    }

    [Fact]
    public void Update_without_a_change_does_not_raise_changed()
    {
        // Arrange
        var store = CreateStore();
        store.Load();
        var raised = false;
        store.Changed += (_, _) => raised = true;

        // Act
        store.Update(s => s with { });

        // Assert
        raised.Should().BeFalse();
    }

    [Fact]
    public void A_failed_save_keeps_the_previous_settings()
    {
        // Arrange
        var store = CreateStore();
        store.Load();
        File.Delete(SettingsFile);
        Directory.CreateDirectory(SettingsFile); // a folder where the file should be: the rename fails

        // Act
        var act = () => store.Update(s => s with { UiLanguage = "ru" });

        // Assert
        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        store.Current.Should().Be(AppSettings.Defaults);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"uiLanguage\": ")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"schemaVersion\": 1, \"uiLanguage\": 42 }")]
    [InlineData("\u0000\u0001garbage")]
    public void Corrupted_file_gives_defaults(string content)
    {
        // Arrange
        WriteSettingsFile(content);
        var store = CreateStore();

        // Act
        var settings = store.Load();

        // Assert
        settings.Should().Be(AppSettings.Defaults);
    }

    [Fact]
    public void Corrupted_file_is_backed_up_with_its_content()
    {
        // Arrange
        const string broken = "{ \"uiLanguage\": \"ru\"";
        WriteSettingsFile(broken);
        var store = CreateStore();

        // Act
        store.Load();

        // Assert
        var backup = SettingsFile + ".20261102-101530.bak";
        File.ReadAllText(backup).Should().Be(broken);
    }

    [Fact]
    public void Corrupted_file_is_replaced_with_defaults_and_logged()
    {
        // Arrange
        WriteSettingsFile("not json");
        var store = CreateStore();

        // Act
        store.Load();

        // Assert
        CreateStore().Load().Should().Be(AppSettings.Defaults);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("corrupted", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_file_gives_defaults_and_is_left_alone()
    {
        // Arrange
        Directory.CreateDirectory(SettingsFile); // reading a folder fails like a locked or forbidden file
        var store = CreateStore();

        // Act
        var settings = store.Load();

        // Assert
        settings.Should().Be(AppSettings.Defaults);
        Directory.Exists(SettingsFile).Should().BeTrue();
        Directory.GetFiles(Paths.Config).Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        // Arrange
        WriteSettingsFile("""{ "schemaVersion": 1, "uiLanguage": "ru", "fromTheFuture": true }""");
        var store = CreateStore();

        // Act
        var settings = store.Load();

        // Assert
        settings.UiLanguage.Should().Be("ru");
    }

    [Fact]
    public void A_file_from_a_newer_app_is_read_and_logged()
    {
        // Arrange
        WriteSettingsFile("""{ "schemaVersion": 99, "uiLanguage": "ru" }""");
        var store = CreateStore();

        // Act
        var settings = store.Load();

        // Assert
        settings.UiLanguage.Should().Be("ru");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("newer", StringComparison.Ordinal));
    }

    [Fact]
    public void An_older_file_is_migrated_and_rewritten()
    {
        // Arrange
        WriteSettingsFile("""{ "schemaVersion": 1, "language": "ru" }""");
        var migrator = new SettingsMigrator(2, new Dictionary<int, Func<JsonObject, JsonObject>>
        {
            [1] = d =>
            {
                d["uiLanguage"] = d["language"]?.DeepClone();
                d.Remove("language");
                return d;
            },
        });
        var store = CreateStore(migrator);

        // Act
        var settings = store.Load();

        // Assert
        settings.Should().Be(AppSettings.Defaults with { SchemaVersion = 2, UiLanguage = "ru" });
        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        json["schemaVersion"]!.GetValue<int>().Should().Be(2);
        json["uiLanguage"]!.GetValue<string>().Should().Be("ru");
    }

    [Fact]
    public void A_current_file_is_not_rewritten_on_load()
    {
        // Arrange
        const string content = """{ "schemaVersion": 1, "uiLanguage": "ru" /* hand-edited */ }""";
        WriteSettingsFile(content);
        var store = CreateStore();

        // Act
        store.Load();

        // Assert
        File.ReadAllText(SettingsFile).Should().Be(content);
    }
}
