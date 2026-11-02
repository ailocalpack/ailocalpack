using AiLocalPack.Desktop.ViewModels.Settings;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class SettingsSearchTests
{
    private sealed record Row(string Label, params string[] Keywords) : ISearchableRow
    {
        public IEnumerable<string> SearchTexts => [Label, .. Keywords];
    }

    private sealed record Group(string Title, params Row[] Items) : ISearchableGroup
    {
        public IEnumerable<string> SearchTexts => [Title];

        public IEnumerable<ISearchableRow> Rows => Items;
    }

    private sealed record Section(string Title, params Group[] Items) : ISearchableSection
    {
        public IEnumerable<string> SearchTexts => [Title];

        public IEnumerable<ISearchableGroup> Groups => Items;
    }

    private static readonly Row Theme = new("Theme", "dark", "light");
    private static readonly Row UiLanguage = new("Interface language", "locale");
    private static readonly Row Tray = new("Keep running in the tray", "autostart");
    private static readonly Row Cache = new("Clear cache");
    private static readonly Row Français = new("Langue de l’interface");

    private static readonly Section General = new(
        "General",
        new Group("Language", UiLanguage),
        new Group("Appearance", Theme),
        new Group("Background", Tray));

    private static readonly Section Storage = new("Storage", new Group("Cache", Cache));

    private static readonly Section Agents = new("AI agents");

    private static IReadOnlyList<(Section Section, IReadOnlyList<Row> Rows)> Run(string query, params Section[] sections) =>
        SettingsSearch.Run<Section, Row>(sections, query);

    [Fact]
    public void Matches_a_row_by_its_label()
    {
        // Arrange
        Section[] sections = [General, Storage];

        // Act
        var results = Run("theme", sections);

        // Assert
        results.Should().ContainSingle();
        results[0].Section.Should().Be(General);
        results[0].Rows.Should().Equal(Theme);
    }

    [Fact]
    public void Matches_a_row_by_its_keywords()
    {
        // Arrange
        Section[] sections = [General, Storage];

        // Act
        var results = Run("autostart", sections);

        // Assert
        results.Should().ContainSingle().Which.Rows.Should().Equal(Tray);
    }

    [Theory]
    [InlineData("THEME")]
    [InlineData("tHeMe")]
    [InlineData("  theme  ")]
    public void Ignores_case_and_surrounding_spaces(string query)
    {
        // Arrange
        Section[] sections = [General];

        // Act
        var results = Run(query, sections);

        // Assert
        results.Should().ContainSingle().Which.Rows.Should().Equal(Theme);
    }

    [Fact]
    public void Ignores_diacritics()
    {
        // Arrange
        var french = new Section("Général", new Group("Langue", Français));

        // Act
        var results = Run("generál", french);

        // Assert
        results.Should().ContainSingle().Which.Section.Should().Be(french);
    }

    [Theory]
    [InlineData("Ёлка", "елка")]
    [InlineData("Café", "cafe")]
    [InlineData("cafe", "CAFÉ")]
    public void Finds_text_regardless_of_diacritics(string text, string query)
    {
        // Arrange
        var expected = (0, text.Length);

        // Act
        var match = SettingsSearch.Find(text, query);

        // Assert
        match.Should().Be(expected);
    }

    [Fact]
    public void Find_gives_the_position_in_the_original_text()
    {
        // Arrange
        const string text = "Keep running in the tray";

        // Act
        var match = SettingsSearch.Find(text, "TRAY");

        // Assert
        match.Should().Be((20, 4));
    }

    [Fact]
    public void Groups_results_by_section_in_section_order()
    {
        // Arrange
        Section[] sections = [General, Storage];

        // Act
        var results = Run("e", sections);

        // Assert
        results.Select(r => r.Section).Should().Equal(General, Storage);
        results[0].Rows.Should().Equal(UiLanguage, Theme, Tray);
        results[1].Rows.Should().Equal(Cache);
    }

    [Fact]
    public void A_matching_group_title_brings_all_its_rows()
    {
        // Arrange
        Section[] sections = [General];

        // Act
        var results = Run("appearance", sections);

        // Assert
        results.Should().ContainSingle().Which.Rows.Should().Equal(Theme);
    }

    [Fact]
    public void A_matching_section_title_brings_the_section_even_without_rows()
    {
        // Arrange
        Section[] sections = [General, Agents];

        // Act
        var results = Run("agents", sections);

        // Assert
        results.Should().ContainSingle();
        results[0].Section.Should().Be(Agents);
        results[0].Rows.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_matches_gives_no_results()
    {
        // Arrange
        Section[] sections = [General, Storage];

        // Act
        var results = Run("xyzzy", sections);

        // Assert
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_query_gives_no_results(string query)
    {
        // Arrange
        Section[] sections = [General, Storage];

        // Act
        var results = Run(query, sections);

        // Assert
        results.Should().BeEmpty();
    }
}
