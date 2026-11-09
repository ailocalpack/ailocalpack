using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The Generation tab's glossary review (task 54, design §6c): rows, edits saved at once, tone, Continue, Regenerate.</summary>
public sealed class GlossaryReviewTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly Glossary Drafted = new("ru", "на ты, игриво", [
        new GlossaryEntry("Harold", "Гарольд", "the hero", "noun", "masculine"),
        new GlossaryEntry("Potion", "Зелье"),
        new GlossaryEntry("Hexa Corp", "Hexa Corp", "a company", KeepAsIs: true),
    ]);

    /// <summary>Steam game 10 "Alpha", Russian, Claude Code ready; the fake stages finish every step at once.</summary>
    private static ServiceProvider Build()
    {
        var services = TestServices.Build(s => s.AddSingleton<TimeProvider>(new FakeClock(Noon)), steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        return services;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    /// <summary>Alpha's job waiting for the review of <see cref="Drafted"/> on disk (no runner holds it).</summary>
    private static string Waiting(ServiceProvider services, Glossary? glossary = null)
    {
        var (entry, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(Alpha(services), "ru", "claude", new GenerationOptions(ReviewGlossary: true));
        jobLock.Dispose();
        GenerationJobStore.Write(entry.Folder, entry.Job with
        {
            Stage = GenerationStage.Glossary,
            Status = GenerationJobStatus.WaitingForReview,
            RunningSince = null,
            Counters = new GenerationCounters(10, 0, 1, 0),
        });
        new GenerationFiles(entry.Folder).WriteGlossary(glossary ?? Drafted);
        return entry.Folder;
    }

    private static async Task<GenerationTabViewModel> OpenAsync(ServiceProvider services)
    {
        var tab = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation).Generation;
        await WaitUntil(() => tab.Review?.IsLoaded == true);
        return tab;
    }

    private static Glossary? OnDisk(string folder) => new GenerationFiles(folder).ReadGlossary();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never came true.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_job_waiting_for_review_shows_the_review_instead_of_the_progress_card()
    {
        // Arrange
        using var services = Build();
        Waiting(services);

        // Act
        var tab = await OpenAsync(services);

        // Assert
        (tab.ShowReview, tab.ShowProgress, tab.ShowBannerAction, tab.Review!.CountText).Should().Be((true, false, false, "3 terms"));
    }

    [Fact]
    public async Task Rows_show_the_term_translation_and_note()
    {
        // Arrange
        using var services = Build();
        Waiting(services);

        // Act
        var tab = await OpenAsync(services);

        // Assert
        tab.Review!.Rows.Select(r => $"{r.Term} | {r.Translation} | {r.Note}").Should().Equal(
            "Harold | Гарольд | noun, masculine · the hero",
            "Potion | Зелье | ",
            "Hexa Corp | Hexa Corp | keep as is · a company");
    }

    [Fact]
    public async Task Tone_shows_the_agents_words_and_its_choice_until_the_player_picks()
    {
        // Arrange
        using var services = Build();
        Waiting(services);

        // Act
        var review = (await OpenAsync(services)).Review!;

        // Assert
        (review.AgentTone, review.SelectedTone?.Name, string.Join(" / ", review.ToneOptions.Select(o => o.Name)))
            .Should().Be(("The agent's tone: на ты, игриво", "As the agent chose", "As the agent chose / Informal («ты») / Formal («вы»)"));
    }

    [Fact]
    public async Task An_edited_translation_is_saved_to_the_job_at_once_and_marked_edited()
    {
        // Arrange
        using var services = Build();
        var folder = Waiting(services);
        var review = (await OpenAsync(services)).Review!;

        // Act
        review.Rows[1].Translation = "Эликсир";

        // Assert
        await WaitUntil(() => review.Rows[1].IsEdited);
        (OnDisk(folder)!.Entries[1], review.Rows[1].IsEdited).Should().Be((new GlossaryEntry("Potion", "Эликсир", Edited: true), true));
    }

    [Fact]
    public async Task Edits_are_read_back_by_a_page_opened_later()
    {
        // Arrange
        using var services = Build();
        Waiting(services);
        var review = (await OpenAsync(services)).Review!;
        review.Rows[0].Translation = "Гаральд";
        await review.FlushAsync();

        // Act
        var reopened = (await OpenAsync(services)).Review!;

        // Assert
        (reopened.Rows[0].Translation, reopened.Rows[0].IsEdited).Should().Be(("Гаральд", true));
    }

    [Fact]
    public async Task A_blank_translation_isnt_saved_and_blocks_continue()
    {
        // Arrange
        using var services = Build();
        var folder = Waiting(services);
        var tab = await OpenAsync(services);

        // Act
        tab.Review!.Rows[0].Translation = " ";
        await tab.Review.FlushAsync();

        // Assert
        (tab.Review.HasBlank, tab.ContinueCommand.CanExecute(null), OnDisk(folder)!.Entries[0].Translation).Should().Be((true, false, "Гарольд"));
    }

    [Fact]
    public async Task Choosing_a_form_of_address_is_saved()
    {
        // Arrange
        using var services = Build();
        var folder = Waiting(services);
        var review = (await OpenAsync(services)).Review!;

        // Act
        review.SelectedTone = review.ToneOptions.Single(o => o.Value == FormOfAddress.Formal);

        // Assert
        await WaitUntil(() => OnDisk(folder)!.FormOfAddress is not null);
        OnDisk(folder)!.FormOfAddress.Should().Be(FormOfAddress.Formal);
    }

    [Fact]
    public async Task Search_keeps_the_matching_rows()
    {
        // Arrange
        using var services = Build();
        Waiting(services);
        var review = (await OpenAsync(services)).Review!;

        // Act
        review.Query = "зел";

        // Assert
        review.Rows.Select(r => r.Term).Should().Equal("Potion");
    }

    [Fact]
    public async Task Continue_saves_the_last_edit_and_starts_the_translation()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeGenerationStages>().Hold = true;
        var folder = Waiting(services);
        var tab = await OpenAsync(services);
        tab.Review!.Rows[1].Translation = "Эликсир";

        // Act
        await tab.ContinueCommand.ExecuteAsync(null);

        // Assert
        var job = services.GetRequiredService<GenerationService>().FindOpen(GameId.Steam(10))!;
        (job.Stage, job.Status, OnDisk(folder)!.Entries[1].Translation, tab.ShowReview, tab.ShowProgress)
            .Should().Be((GenerationStage.Translate, GenerationJobStatus.Running, "Эликсир", false, true));
    }

    [Fact]
    public async Task Regenerate_asks_first()
    {
        // Arrange
        using var services = Build();
        Waiting(services);
        var tab = await OpenAsync(services);
        tab.Review!.Rows[1].Translation = "Эликсир";
        await tab.Review.FlushAsync();

        // Act
        tab.RegenerateCommand.Execute(null);

        // Assert
        (services.GetRequiredService<IDialogService>().Current as ConfirmDialogViewModel)?.Message.Should()
            .Be("The agent drafts it again from the game's strings; the other terms are replaced, the 1 term you changed stays.");
    }

    [Fact]
    public async Task Regenerate_runs_the_glossary_stage_again_with_only_the_players_edits()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeGenerationStages>().Hold = true;
        var folder = Waiting(services);
        var tab = await OpenAsync(services);
        tab.Review!.Rows[1].Translation = "Эликсир";
        await tab.Review.FlushAsync();
        tab.RegenerateCommand.Execute(null);
        var dialog = (ConfirmDialogViewModel)services.GetRequiredService<IDialogService>().Current!;

        // Act
        dialog.ConfirmCommand.Execute(null);

        // Assert
        await WaitUntil(() => tab.Job?.Status == GenerationJobStatus.Running);
        (tab.Job!.Stage, tab.ShowReview, OnDisk(folder)!.Entries.Select(e => e.Term).Single()).Should().Be((GenerationStage.Glossary, false, "Potion"));
    }

    [Fact]
    public async Task A_glossary_that_cant_be_read_shows_why()
    {
        // Arrange
        using var services = Build();
        var folder = Waiting(services);
        File.WriteAllText(new GenerationFiles(folder).GlossaryPath, "not json");

        // Act
        var tab = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation).Generation;
        await WaitUntil(() => tab.Review?.IsFailed == true);

        // Assert
        (tab.Review!.Error.StartsWith("Couldn't read the glossary: glossary.json: not valid JSON", StringComparison.Ordinal), tab.ContinueCommand.CanExecute(null))
            .Should().Be((true, false));
    }
}
