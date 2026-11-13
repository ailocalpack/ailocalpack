using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// The start form in update mode (task 56): a game update made the installed «Фонарщики» rev 3 incompatible, so the
/// form translates only the new and changed strings on top of it.
/// </summary>
public sealed class GenerationUpdateModeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static PackManifest BaseManifest => new(
        "lanterns",
        GameId.Steam(10),
        "ru",
        "Фонарщики",
        [new PackAuthor("alice")],
        [new PackRevision(1, T0.AddDays(-3), "alice"), new PackRevision(2, T0.AddDays(-2), "alice"), new PackRevision(3, T0.AddDays(-1), "alice")],
        PackStatus.Verified,
        null,
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        new PackStringsFile(PackStringsFile.DefaultPath, new string('b', 64), 10),
        null,
        [],
        [],
        new PackCoverage(2, 2),
        T0.AddDays(-3),
        T0.AddDays(-1));

    private static StringTable BaseTable { get; } = new("ru", new Dictionary<string, string> { ["a"] = "Зелье", ["b"] = "Эфир" });

    private static UpdateBaseLoad Loaded => new(new UpdateBase(BaseManifest, BaseTable));

    private static UpdateDiff Diff => new(["c", "d"], ["b"], ["gone"], ["a"]);

    /// <summary>
    /// Steam game 10 "Alpha" with "lanterns" rev 3 installed for Russian (incompatible unless <paramref name="problem"/>
    /// says otherwise), Claude Code ready, the base as <paramref name="load"/> gives it, the estimate counting the delta.
    /// </summary>
    private static ServiceProvider Build(
        InstalledPackProblem problem = InstalledPackProblem.Incompatible,
        Func<Game, string, Task<UpdateBaseLoad?>>? load = null,
        bool forOtherVersion = false)
    {
        var installed = new FakeInstalledPacks();
        installed.Packs[(GameId.Steam(10), "ru")] = new InstalledPackState("lanterns", problem, 3, Local: true, Title: "Фонарщики", ForOtherVersion: forOtherVersion);
        var services = TestServices.Build(s => s.AddSingleton<IInstalledPacks>(installed), steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        services.GetRequiredService<FakeUpdateBases>().Load = load ?? ((_, _) => Task.FromResult<UpdateBaseLoad?>(Loaded));
        services.GetRequiredService<FakeEstimates>().Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(strings: 3) with { Update = Diff });
        return services;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    private static GenerationTabViewModel Open(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation).Generation;

    [Fact]
    public void An_incompatible_pack_puts_the_form_in_update_mode()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        new { tab.IsUpdateMode, tab.FormTitle, tab.StartText, tab.CanPickLanguage }.Should().Be(new
        {
            IsUpdateMode = true,
            FormTitle = "Translate the new strings at",
            StartText = "Translate new strings",
            CanPickLanguage = false,
        });
    }

    [Fact]
    public void The_banner_names_the_base_pack_and_what_changed()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        (tab.UpdateBannerText, tab.UpdateDiffText).Should().Be((
            "Based on the installed «Фонарщики» rev 3: its translations stay, only new and changed strings go to the agent.",
            "New: 2 · Changed: 1 · Kept: 1 · Removed: 1"));
    }

    [Fact]
    public void The_estimate_is_given_the_base_strings()
    {
        // Arrange
        using var services = Build();

        // Act
        Open(services);

        // Assert
        services.GetRequiredService<FakeEstimates>().LastBase.Should().BeSameAs(BaseTable);
    }

    [Fact]
    public void A_pack_made_for_another_version_puts_the_form_in_update_mode()
    {
        // Arrange
        using var services = Build(problem: InstalledPackProblem.None, forOtherVersion: true);

        // Act
        var tab = Open(services);

        // Assert
        new { tab.IsUpdateMode, tab.StartText }.Should().Be(new { IsUpdateMode = true, StartText = "Translate new strings" });
    }

    [Fact]
    public void A_pack_made_for_another_version_shows_the_banner_with_translate_new_strings()
    {
        // Arrange
        using var services = Build(problem: InstalledPackProblem.None, forOtherVersion: true);

        // Act
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services));

        // Assert
        new { page.ShowUpdateBanner, page.ShowBannerTranslate, page.UpdateBannerText, Action = page.Action.Kind }.Should().Be(new
        {
            ShowUpdateBanner = true,
            ShowBannerTranslate = true,
            UpdateBannerText = "The game updated (build 1,000). The installed «Фонарщики» rev 3 was made for an earlier version: strings the update added or changed show untranslated.",
            Action = GamePrimaryActionKind.Play,
        });
    }

    [Fact]
    public void A_compatible_pack_leaves_the_form_as_it_is()
    {
        // Arrange
        using var services = Build(problem: InstalledPackProblem.None);

        // Act
        var tab = Open(services);

        // Assert
        new { tab.IsUpdateMode, tab.FormTitle, Loads = services.GetRequiredService<FakeUpdateBases>().Calls }
            .Should().Be(new { IsUpdateMode = false, FormTitle = "Create a", Loads = 0 });
    }

    [Fact]
    public void A_base_without_strings_makes_a_new_translation()
    {
        // Arrange
        using var services = Build(load: (_, _) => Task.FromResult<UpdateBaseLoad?>(null));

        // Act
        var tab = Open(services);

        // Assert
        new { tab.IsUpdateMode, tab.IsEstimated, Base = services.GetRequiredService<FakeEstimates>().LastBase }
            .Should().BeEquivalentTo(new { IsUpdateMode = false, IsEstimated = true, Base = (StringTable?)null });
    }

    [Fact]
    public void A_base_that_cant_be_read_says_why_and_holds_start()
    {
        // Arrange
        using var services = Build(load: (_, _) => Task.FromResult<UpdateBaseLoad?>(new UpdateBaseLoad(null, UpdateBaseProblem.Missing, "not in the pack cache")));

        // Act
        var tab = Open(services);

        // Assert
        (tab.EstimateErrorText, tab.StartBlock).Should().Be(("The installed «Фонарщики» rev 3 can’t be read: not in the pack cache", GenerationStartBlock.EstimateFailed));
    }

    [Fact]
    public async Task Start_makes_an_update_job_of_the_base_pack()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        var job = services.GetRequiredService<FakeGenerationStages>().Jobs.Single();
        (job.Language, job.Options.Base).Should().Be(("ru", new GenerationBase("lanterns", 3, false, "Фонарщики")));
    }

    [Fact]
    public async Task Start_copies_the_base_pack_into_the_job_folder()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        var folder = services.GetRequiredService<GenerationService>().FolderOf(GameId.Steam(10))!;
        new GenerationFiles(folder).ReadBase()!.Strings.Strings.Should().BeEquivalentTo(BaseTable.Strings);
    }
}
