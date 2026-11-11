using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The Packs tab's Mine section (task 55): the generation job and the packs generated on this machine.</summary>
public sealed class PacksTabMineTests
{
    private static GamePageViewModel Page(InstallBed bed) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, GamePageTab.Packs);

    [Fact]
    public async Task Local_pack_is_listed_in_mine()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("full"));
        bed.LocalPack();

        // Act
        var packs = Page(bed).Packs;

        // Assert
        packs.Mine.Select(r => (r.Title, r.Meta, r.Tag)).Should().Equal(("Claude Code translation", "local · not published", "not tried yet"));
    }

    [Fact]
    public async Task Registry_pack_stays_selected_next_to_mine()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("full"));
        bed.LocalPack();

        // Act
        var packs = Page(bed).Packs;

        // Assert
        (packs.ShowPackDetails, packs.Selected?.Pack.Id).Should().Be((true, "full"));
    }

    [Fact]
    public async Task Only_local_packs_select_the_first_one()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var packs = Page(bed).Packs;

        // Assert
        (packs.ShowList, packs.ShowEmpty, packs.ShowMineDetails, packs.ShowRegistryNone).Should().Be((true, false, true, true));
    }

    [Fact]
    public async Task Selecting_a_local_pack_shows_its_details()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("full"));
        bed.LocalPack(tried: new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero));
        var packs = Page(bed).Packs;

        // Act
        packs.Mine[0].SelectCommand.Execute(null);

        // Assert
        var mine = packs.SelectedMine!;
        (packs.ShowMineDetails, packs.ShowPackDetails, mine.TriedTitle, mine.AgentText, mine.TestRunText)
            .Should().Be((true, false, "Tested in game", "Claude Code (claude-opus-5-5)", "Passed: The translated game ran 15 s without closing"));
    }

    [Fact]
    public async Task Pack_in_another_language_names_it()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack(language: "de");

        // Act
        var row = Page(bed).Packs.Mine.Single();

        // Assert
        row.Meta.Should().Be("local · not published · Deutsch");
    }

    [Fact]
    public async Task Open_job_is_listed_as_creating()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await bed.Services.GetRequiredService<GenerationService>().StartAsync(bed.Game, "ru", "claude", GenerationOptions.Default, null);

        // Act
        var row = Page(bed).Packs.Mine.Single();

        // Assert
        (row.IsCreating, row.Tag).Should().Be((true, "creating 0%"));
    }

    [Fact]
    public async Task Creating_row_opens_the_generation_tab()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        await bed.Services.GetRequiredService<GenerationService>().StartAsync(bed.Game, "ru", "claude", GenerationOptions.Default, null);
        var page = Page(bed);

        // Act
        page.Packs.Mine.Single().SelectCommand.Execute(null);

        // Assert
        page.SelectedTab.Should().Be(GamePageTab.Generation);
    }

    [Fact]
    public async Task Install_from_mine_marks_the_pack_installed()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        var page = Page(bed);

        // Act
        page.Packs.Mine.Single().InstallCommand.Execute(null);

        // Assert
        await Until(() => page.Packs.Mine.SingleOrDefault()?.InstallText == "Installed");
        (page.Packs.Mine.Single().InstallText, page.Packs.Mine.Single().CanDelete, page.InstalledText)
            .Should().Be(("Installed", false, "«Claude Code translation» · your translation"));
    }

    [Fact]
    public async Task Untried_pack_waits_to_be_tried_before_publishing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var row = Page(bed).Packs.Mine.Single();

        // Assert
        (row.PublishCommand.CanExecute(null), row.ActionHint).Should().Be((false, "Try it in game first: publishing opens after that."));
    }

    [Fact]
    public async Task Delete_removes_the_pack_from_mine()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var page = Page(bed);

        // Act
        await page.DeleteLocalAsync(pack);

        // Assert
        (page.Packs.HasMine, bed.Services.GetRequiredService<LocalPackStore>().List(InstallBed.Alpha).Count).Should().Be((false, 0));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
