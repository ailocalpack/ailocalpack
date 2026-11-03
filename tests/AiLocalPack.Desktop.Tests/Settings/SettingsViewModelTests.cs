using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private SettingsViewModel Create(SettingsSectionId? section = null) =>
        section is { } id
            ? ActivatorUtilities.CreateInstance<SettingsViewModel>(_services, id)
            : ActivatorUtilities.CreateInstance<SettingsViewModel>(_services);

    [Fact]
    public void Lists_every_section_in_order()
    {
        // Arrange
        var vm = Create();

        // Act
        var sections = vm.Sections.Select(s => s.Id);

        // Assert
        sections.Should().Equal(Enum.GetValues<SettingsSectionId>());
        vm.Sections.Select(s => s.Title).Should().Equal("General", "Libraries", "AI agents", "GitHub", "Storage", "About");
    }

    [Fact]
    public void Opens_general_by_default()
    {
        // Arrange
        var vm = Create();

        // Act
        var selected = vm.NavSelection;

        // Assert
        vm.SelectedSection.Id.Should().Be(SettingsSectionId.General);
        selected.Should().BeSameAs(vm.SelectedSection);
        vm.IsSearching.Should().BeFalse();
    }

    [Fact]
    public void Deep_link_opens_the_section()
    {
        // Arrange
        const SettingsSectionId section = SettingsSectionId.Agents;

        // Act
        var vm = Create(section);

        // Assert
        vm.SelectedSection.Id.Should().Be(section);
        vm.SelectedSection.IsComingSoon.Should().BeTrue();
    }

    [Fact]
    public void Show_section_selects_it_and_leaves_the_search()
    {
        // Arrange
        var vm = Create();
        vm.Query = "theme";

        // Act
        vm.ShowSection(SettingsSectionId.About);

        // Assert
        vm.SelectedSection.Id.Should().Be(SettingsSectionId.About);
        vm.Query.Should().BeEmpty();
    }

    [Fact]
    public void Typing_a_query_shows_matching_rows_under_their_section()
    {
        // Arrange
        var vm = Create();
        var general = (GeneralSettingsSection)vm.Sections[0];

        // Act
        vm.Query = "theme";

        // Assert
        vm.IsSearching.Should().BeTrue();
        vm.NavSelection.Should().BeNull();
        vm.Results.Should().ContainSingle();
        vm.Results[0].Section.Should().BeSameAs(general);
        vm.Results[0].Rows.Should().Equal(general.Theme);
        vm.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void Search_finds_rows_by_keywords()
    {
        // Arrange
        var vm = Create();
        var general = (GeneralSettingsSection)vm.Sections[0];

        // Act
        vm.Query = "autostart";

        // Assert
        vm.Results.Should().ContainSingle().Which.Rows.Should().Equal(general.RunInBackground);
    }

    [Fact]
    public void A_query_without_matches_says_so()
    {
        // Arrange
        var vm = Create();

        // Act
        vm.Query = " xyzzy ";

        // Assert
        vm.Results.Should().BeEmpty();
        vm.HasNoResults.Should().BeTrue();
        vm.NoResultsText.Should().Be("No settings match “xyzzy”.");
    }

    [Fact]
    public void Picking_a_section_in_the_list_opens_it_and_clears_the_search()
    {
        // Arrange
        var vm = Create();
        vm.Query = "theme";

        // Act
        vm.NavSelection = vm.Sections[4];

        // Assert
        vm.SelectedSection.Id.Should().Be(SettingsSectionId.Storage);
        vm.Query.Should().BeEmpty();
        vm.NavSelection.Should().BeSameAs(vm.Sections[4]);
    }

    [Fact]
    public void Section_link_in_results_opens_the_section()
    {
        // Arrange
        var vm = Create(SettingsSectionId.About);
        vm.Query = "theme";

        // Act
        vm.OpenSectionCommand.Execute(vm.Results[0].Section);

        // Assert
        vm.SelectedSection.Id.Should().Be(SettingsSectionId.General);
        vm.IsSearching.Should().BeFalse();
    }

    [Fact]
    public void Escape_clears_the_search()
    {
        // Arrange
        var vm = Create();
        vm.Query = "theme";

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeTrue();
        vm.Query.Should().BeEmpty();
        vm.Results.Should().BeEmpty();
    }

    [Fact]
    public void Escape_without_a_search_is_left_to_the_window()
    {
        // Arrange
        var vm = Create();

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeFalse();
    }

    [Fact]
    public void Shows_the_app_version()
    {
        // Arrange
        var vm = Create();

        // Act
        var version = vm.Version;

        // Assert
        version.Should().MatchRegex(@"^ailocalpack \d+\.\d+\.\d+");
        version.Should().NotContain("+");
    }
}
