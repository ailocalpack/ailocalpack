using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The start form's translation quality (Quick / Balanced / Thorough, decided 2026-11-13): the title's mode button, its popover, Compare modes.</summary>
public sealed class GenerationQualityTests
{
    private static ServiceProvider Build(string agent = "claude")
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru", DefaultAgent = agent });
        services.GetRequiredService<FakeAgentHost>().Probes[agent] = FakeAgentHost.SignedIn(agent);
        return services;
    }

    private static GenerationTabViewModel Open(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(
            services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10)),
            GamePageTab.Generation).Generation;

    [Fact]
    public void Balanced_is_chosen_by_default()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        (tab.Quality, tab.QualityName, tab.QualityOptions.Single(o => o.IsSelected).Quality, tab.ShowWebLookup)
            .Should().Be((GenerationQuality.Balanced, "Balanced", GenerationQuality.Balanced, false));
    }

    [Fact]
    public void The_title_reads_as_a_sentence_around_the_mode_button()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        (tab.FormTitle, tab.QualityName, tab.FormTitleAfter).Should().Be(("Create a", "Balanced", "translation into"));
    }

    [Fact]
    public void The_model_line_names_claude_codes_model_for_the_quality()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        tab.QualityOptions.Single(o => o.Quality == GenerationQuality.Thorough).SelectCommand.Execute(null);

        // Assert
        tab.QualityModelLine.Should().Be("Studies the game and translates with Opus");
    }

    [Fact]
    public void Codex_keeps_its_default_model_and_names_the_effort()
    {
        // Arrange
        using var services = Build("codex");

        // Act
        var tab = Open(services);

        // Assert
        tab.QualityOptions.Select(o => o.Model).Should().Equal("default model, low effort", "default model, medium effort", "default model, high effort");
    }

    [Fact]
    public void Thorough_adds_the_study_step_and_the_web_lookup()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        tab.Quality = GenerationQuality.Thorough;

        // Assert
        (string.Join(", ", tab.Steps.Select(s => s.Name)), tab.ShowWebLookup)
            .Should().Be(("Setup, Extract, Study, Glossary, Translate, Build & test", true));
    }

    [Fact]
    public void The_tiles_follow_the_quality()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeEstimates>().Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(strings: 12_384, words: 180_412));
        var tab = Open(services);
        var balanced = (tab.TimeValue, tab.LimitsValue);

        // Act
        tab.Quality = GenerationQuality.Quick;

        // Assert
        (tab.TimeValue, tab.LimitsValue).Should().NotBe(balanced);
    }

    [Fact]
    public void Every_row_of_the_popover_shows_its_time_once_estimated()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeEstimates>().Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(strings: 12_384, words: 180_412));

        // Act
        var tab = Open(services);

        // Assert
        tab.QualityOptions.Should().AllSatisfy(o => o.Time.Should().NotBeEmpty());
    }

    [Fact]
    public async Task Start_passes_the_quality_and_the_web_lookup()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        tab.Quality = GenerationQuality.Thorough;
        tab.WebLookup = true;

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        var options = services.GetRequiredService<FakeGenerationStages>().Jobs.Single().Options;
        (options.Quality, options.WebLookup).Should().Be((GenerationQuality.Thorough, true));
    }

    [Fact]
    public async Task Web_lookup_is_never_on_for_another_quality()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        tab.WebLookup = true;
        tab.Quality = GenerationQuality.Quick;

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        var options = services.GetRequiredService<FakeGenerationStages>().Jobs.Single().Options;
        (options.Quality, options.WebLookup).Should().Be((GenerationQuality.Quick, false));
    }

    [Fact]
    public async Task A_thorough_job_shows_its_quality_next_to_the_stage()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        tab.Quality = GenerationQuality.Thorough;

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        tab.QualityChip.Should().Be("Thorough · Opus");
    }

    [Fact]
    public void Compare_modes_opens_the_dialog_with_the_current_quality_marked()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        tab.CompareQualitiesCommand.Execute(null);

        // Assert
        var dialog = services.GetRequiredService<IDialogService>().Current.Should().BeOfType<GenerationQualityDialogViewModel>().Subject;
        dialog.Columns.Single(c => c.IsCurrent).Quality.Should().Be(GenerationQuality.Balanced);
    }

    [Fact]
    public void Choosing_in_the_compare_dialog_sets_the_quality_and_closes_it()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        tab.CompareQualitiesCommand.Execute(null);
        var dialog = (GenerationQualityDialogViewModel)services.GetRequiredService<IDialogService>().Current!;

        // Act
        dialog.Columns.Single(c => c.Quality == GenerationQuality.Thorough).ChooseCommand.Execute(null);

        // Assert
        (tab.Quality, services.GetRequiredService<IDialogService>().Current).Should().Be((GenerationQuality.Thorough, (DialogViewModel?)null));
    }

    [Fact]
    public void The_compare_dialog_has_a_cell_for_every_row()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);

        // Act
        tab.CompareQualitiesCommand.Execute(null);

        // Assert
        var dialog = (GenerationQualityDialogViewModel)services.GetRequiredService<IDialogService>().Current!;
        dialog.Columns.Should().AllSatisfy(c => c.Cells.Should().HaveCount(dialog.RowNames.Count));
    }

    [Fact]
    public void Steps_of_a_thorough_job_at_study_mark_extract_done()
    {
        // Arrange
        var now = DateTimeOffset.UnixEpoch;
        var job = new GenerationJob
        {
            Id = "j",
            Game = GameId.Steam(10),
            GameName = "Alpha",
            Language = "ru",
            Agent = "claude",
            Options = new GenerationOptions(Quality: GenerationQuality.Thorough),
            Stage = GenerationStage.Study,
            Created = now,
            Updated = now,
        };

        // Act
        var states = GenerationSteps.Of(job);

        // Assert
        states.Should().Equal(
            GenerationStepState.Done,
            GenerationStepState.Done,
            GenerationStepState.Running,
            GenerationStepState.Upcoming,
            GenerationStepState.Upcoming,
            GenerationStepState.Upcoming);
    }
}
