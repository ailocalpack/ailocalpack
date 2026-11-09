using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The Generation tab's progress card (task 53): the job's state → banner, step icons, card; actions and the cancel dialog.</summary>
public sealed class GenerationProgressTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Steam game 10 "Alpha", target language Russian, Claude Code ready; the clock at noon UTC (zone UTC).</summary>
    private static ServiceProvider Build(FakeClock? clock = null)
    {
        clock ??= new FakeClock(Noon);
        var services = TestServices.Build(s => s.AddSingleton<TimeProvider>(clock), steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        return services;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    private static GenerationTabViewModel Open(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation).Generation;

    /// <summary>A job of Alpha saved on disk in the state <paramref name="change"/> gives it (no runner holds it).</summary>
    private static GenerationJobEntry JobOnDisk(ServiceProvider services, Func<GenerationJob, GenerationJob> change)
    {
        var (entry, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(Alpha(services), "ru", "claude", GenerationOptions.Default);
        jobLock.Dispose();
        var job = change(entry.Job);
        GenerationJobStore.Write(entry.Folder, job);
        return entry with { Job = job };
    }

    private static GenerationJob Translating(GenerationJob job, GenerationJobStatus status = GenerationJobStatus.Running) => job with
    {
        Stage = GenerationStage.Translate,
        Status = status,
        Counters = new GenerationCounters(100, 30, 10, 3),
        ChunkTime = TimeSpan.FromMinutes(3),
    };

    private static async Task<GenerationTabViewModel> StartedAsync(ServiceProvider services)
    {
        var tab = Open(services);
        await tab.StartCommand.ExecuteAsync(null);
        return tab;
    }

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

    private static string Clock(int hour, string format = "t") =>
        new DateTime(2026, 11, 9, hour, 0, 0).ToString(format, CultureInfo.GetCultureInfo("en"));

    // ---------- State → banner, controls, step icons ----------

    [Fact]
    public async Task Running_job_has_no_banner_and_offers_pause_and_cancel()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = await StartedAsync(services);

        // Assert
        (tab.HasBanner, tab.ShowPause, tab.ShowResume, tab.CancelCommand.CanExecute(null), tab.IsBarPaused).Should().Be((false, true, false, true, false));
    }

    [Theory]
    [InlineData(GenerationJobStatus.Paused, GenerationPauseReason.Limit, GenerationBanner.Limit, "IconPause", "Resume now")]
    [InlineData(GenerationJobStatus.Paused, GenerationPauseReason.Player, GenerationBanner.Paused, "IconPause", "Resume")]
    [InlineData(GenerationJobStatus.Interrupted, null, GenerationBanner.Interrupted, "IconAlert", "Resume")]
    [InlineData(GenerationJobStatus.Failed, null, GenerationBanner.Failed, "IconXCircle", "Retry stage")]
    [InlineData(GenerationJobStatus.WaitingForReview, null, GenerationBanner.Review, "IconPencil", "Continue to translation")]
    public void Each_stopped_state_has_its_banner_icon_and_action(
        GenerationJobStatus status, GenerationPauseReason? reason, GenerationBanner banner, string icon, string action)
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, status) with
        {
            PauseReason = reason,
            Failure = status == GenerationJobStatus.Failed ? new GenerationFailure(GenerationStage.Translate, GenerationFailureKind.Agent, "x") : null,
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.Banner, tab.BannerIcon, tab.BannerActionText, tab.ShowPause, tab.IsBarPaused).Should().Be((banner, icon, action, false, true));
    }

    [Theory]
    [InlineData(GenerationJobStatus.Paused, GenerationPauseReason.Limit, true, false, false)]
    [InlineData(GenerationJobStatus.Paused, GenerationPauseReason.Player, true, false, false)]
    [InlineData(GenerationJobStatus.Interrupted, null, false, true, false)]
    [InlineData(GenerationJobStatus.Failed, null, false, false, true)]
    [InlineData(GenerationJobStatus.WaitingForReview, null, false, true, false)]
    public void Banner_tone_follows_the_state(GenerationJobStatus status, GenerationPauseReason? reason, bool gen, bool warn, bool bad)
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, status) with { PauseReason = reason });

        // Act
        var tab = Open(services);

        // Assert
        (tab.IsBannerGen, tab.IsBannerWarn, tab.IsBannerBad).Should().Be((gen, warn, bad));
    }

    [Theory]
    [InlineData(GenerationJobStatus.Running, GenerationStage.Translate, 3, "IconLoader")]
    [InlineData(GenerationJobStatus.Paused, GenerationStage.Translate, 3, "IconPause")]
    [InlineData(GenerationJobStatus.Interrupted, GenerationStage.Glossary, 2, "IconPause")]
    [InlineData(GenerationJobStatus.WaitingForReview, GenerationStage.Glossary, 2, "IconPencil")]
    [InlineData(GenerationJobStatus.Failed, GenerationStage.Build, 4, "IconX")]
    public void Current_step_shows_the_state_s_icon_and_earlier_steps_are_done(GenerationJobStatus status, GenerationStage stage, int index, string icon)
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => j with
        {
            Stage = stage,
            Status = status,
            PauseReason = status == GenerationJobStatus.Paused ? GenerationPauseReason.Player : null,
            Failure = status == GenerationJobStatus.Failed ? new GenerationFailure(stage, GenerationFailureKind.TestRun, "x") : null,
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.Steps[index].Icon, tab.Steps.Take(index).All(s => s.IsDone), tab.Steps.Skip(index + 1).All(s => !s.IsReached))
            .Should().Be((icon, true, true));
    }

    [Fact]
    public void Limit_banner_says_when_the_job_resumes_by_itself()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Paused) with { PauseReason = GenerationPauseReason.Limit, ResumeAt = Noon.AddHours(6) });

        // Act
        var tab = Open(services);

        // Assert
        (tab.BannerTitle, tab.BannerText).Should().Be(("Your agent subscription limit was reached.", $"The job resumes automatically at ~{Clock(18)}."));
    }

    [Fact]
    public void Interrupted_banner_names_the_stage_and_chunk_to_resume_from()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Interrupted));

        // Act
        var tab = Open(services);

        // Assert
        (tab.BannerTitle, tab.BannerText).Should().Be(("The app was closed during the job.", "Progress is saved: resume from stage Translate, chunk 4 of 10."));
    }

    [Fact]
    public void Failed_banner_words_the_failure_s_kind_and_offers_details()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => j with
        {
            Stage = GenerationStage.Build,
            Status = GenerationJobStatus.Failed,
            Failure = new GenerationFailure(GenerationStage.Build, GenerationFailureKind.TestRun, "Test run failed: exit code 3"),
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.BannerTitle, tab.BannerText, tab.ShowBannerDetails, tab.ShowRetry).Should().Be((
            "Stage Build & test failed.",
            "The translated game closed with an error during the test run. Retrying rebuilds the files; your translations are kept.",
            true,
            true));
    }

    [Fact]
    public void Signed_out_agent_failure_names_the_agent()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => j with
        {
            Stage = GenerationStage.Glossary,
            Status = GenerationJobStatus.Failed,
            Failure = new GenerationFailure(GenerationStage.Glossary, GenerationFailureKind.SignedOut, "signed out"),
        });

        // Act
        var tab = Open(services);

        // Assert
        tab.BannerText.Should().Be("Claude Code is signed out. Sign in in Settings → AI agents, then retry.");
    }

    // ---------- Card ----------

    [Fact]
    public void Translating_shows_the_chunk_and_the_time_left()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j));

        // Act
        var tab = Open(services);

        // Assert
        (tab.PercentText, tab.ProgressTitle, tab.ProgressDetail).Should().Be(("41%", "Translating", "Chunk 4 of 10 · ≈ 12 min left"));
    }

    [Fact]
    public void Stopped_job_shows_no_time_left()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Interrupted));

        // Act
        var tab = Open(services);

        // Assert
        tab.ProgressDetail.Should().Be("Chunk 4 of 10");
    }

    [Fact]
    public async Task Just_translated_lines_show_original_translation_and_warning()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => Translating(j));
        PipelineProgressFile.Update(new GenerationFiles(entry.Folder), p => p with
        {
            Recent = [new RecentTranslation("menu.start", "Start", "Начать"), new RecentTranslation("name.hexa", "Hexa", "Hexa", "untranslated")],
        });
        var tab = Open(services);

        // Act
        await tab.RefreshDetailsAsync();

        // Assert
        (tab.ShowRecent, tab.RecentTitle, tab.Recent[1]).Should().Be((true, "Just translated", new GenerationRecentRow("name.hexa", "Hexa", "Hexa", "same as original")));
    }

    [Fact]
    public async Task Validator_box_counts_the_warnings_and_lists_them_when_shown()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => Translating(j));
        PipelineProgressFile.Update(new GenerationFiles(entry.Folder), p => p with
        {
            Warnings = [new TranslationWarning("a", "whitespace", "A ", "Б"), new TranslationWarning("b", "line-breaks", "B\nB", "В")],
        });
        var tab = Open(services);
        await tab.RefreshDetailsAsync();

        // Act
        tab.ToggleWarningsCommand.Execute(null);

        // Assert
        (tab.WarningCountText, tab.ShowWarnings, tab.WarningsToggleText, tab.Warnings[1].Rule).Should().Be(("2 validator warnings", true, "Hide", "line breaks"));
    }

    [Fact]
    public async Task Limit_box_shows_the_share_the_agent_reported()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => Translating(j));
        PipelineProgressFile.Update(new GenerationFiles(entry.Folder), p => p with { Limit = new AgentLimitInfo(0.45, "five_hour", null) });
        var tab = Open(services);

        // Act
        await tab.RefreshDetailsAsync();

        // Assert
        (tab.LimitTitle, tab.LimitValue, tab.LimitPercent, tab.LimitNote).Should().Be(("Claude Code limit", "~45% used", 45d, "Current 5-hour window, as the agent reports it"));
    }

    [Fact]
    public void Limit_box_of_a_job_paused_by_the_limit_says_reached_and_when_it_resets()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Paused) with { PauseReason = GenerationPauseReason.Limit, ResumeAt = Noon.AddHours(6) });

        // Act
        var tab = Open(services);

        // Assert
        (tab.LimitValue, tab.LimitPercent, tab.LimitNote).Should().Be(("reached", 100d, $"Resets at ~{Clock(18)}"));
    }

    [Fact]
    public void Limit_box_of_an_agent_that_doesnt_report_says_so()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j) with { Agent = "codex" });

        // Act
        var tab = Open(services);

        // Assert
        (tab.HasLimit, tab.LimitNote).Should().Be((false, "Codex doesn't report its limits; the job pauses by itself if they run out."));
    }

    [Fact]
    public async Task Build_stage_shows_its_step_and_the_font_swap()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => j with { Stage = GenerationStage.Build });
        PipelineProgressFile.Update(new GenerationFiles(entry.Folder), p => p with
        {
            Build = new BuildProgress(BuildStep.TestRun, 12, ["Orbitron"], "Noto Sans"),
        });
        var tab = Open(services);

        // Act
        await tab.RefreshDetailsAsync();

        // Assert
        (tab.ShowBuild, tab.BuildStepText, tab.BuildFontNote).Should().Be((
            true, "Launching the test build", "Fonts: “Orbitron” lack letters of the translation; Noto Sans (OFL) is used instead."));
    }

    [Fact]
    public async Task Agent_activity_shows_the_log_in_the_player_s_time()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => Translating(j));
        Directory.CreateDirectory(Path.GetDirectoryName(GenerationJobStore.LogFile(entry.Folder))!);
        File.WriteAllText(GenerationJobStore.LogFile(entry.Folder), "2026-11-09T17:00:00Z Tool call: put_translations\n");
        var tab = Open(services);

        // Act
        await tab.RefreshDetailsAsync();

        // Assert
        tab.LogText.Should().Be($"{Clock(17, "T")} Tool call: put_translations");
    }

    // ---------- Actions ----------

    [Fact]
    public async Task Pause_stops_the_running_job_as_paused_by_the_player()
    {
        // Arrange
        using var services = Build();
        var tab = await StartedAsync(services);
        var jobs = services.GetRequiredService<GenerationService>();

        // Act
        tab.PauseCommand.Execute(null);

        // Assert
        await WaitUntil(() => jobs.FindOpen(GameId.Steam(10))?.Status == GenerationJobStatus.Paused);
        jobs.FindOpen(GameId.Steam(10))!.PauseReason.Should().Be(GenerationPauseReason.Player);
    }

    [Fact]
    public async Task Resume_goes_on_with_an_interrupted_job()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Interrupted));
        var tab = Open(services);

        // Act
        await tab.ResumeAsync();

        // Assert
        (tab.Job?.Status, tab.Job?.Stage, tab.HasBanner, services.GetRequiredService<FakeGenerationStages>().Jobs.Count)
            .Should().Be((GenerationJobStatus.Running, GenerationStage.Translate, false, 1));
    }

    [Fact]
    public async Task Resume_of_a_job_another_command_runs_says_why()
    {
        // Arrange
        using var services = Build();
        var entry = JobOnDisk(services, j => Translating(j, GenerationJobStatus.Interrupted));
        var tab = Open(services);
        using var cli = GenerationJobLock.TryAcquire(entry.Folder);

        // Act
        await tab.ResumeAsync();

        // Assert
        (tab.ActionError, tab.Job?.Status).Should().Be(("The job is running in another window or command.", GenerationJobStatus.Interrupted));
    }

    [Fact]
    public void Cancel_asks_with_the_share_done()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => Translating(j, GenerationJobStatus.Interrupted));
        var tab = Open(services);

        // Act
        tab.CancelCommand.Execute(null);

        // Assert
        (services.GetRequiredService<IDialogService>().Current as GenerationCancelDialogViewModel)?.Message
            .Should().Be("41% is done. You can keep the progress and continue later, or delete the working copy and free the disk.");
    }

    [Fact]
    public async Task Keep_progress_pauses_the_running_job()
    {
        // Arrange
        using var services = Build();
        var tab = await StartedAsync(services);
        tab.CancelCommand.Execute(null);
        var dialog = (GenerationCancelDialogViewModel)services.GetRequiredService<IDialogService>().Current!;

        // Act
        dialog.KeepCommand.Execute(null);

        // Assert
        await WaitUntil(() => services.GetRequiredService<GenerationService>().FindOpen(GameId.Steam(10))?.Status == GenerationJobStatus.Paused);
        services.GetRequiredService<ToastService>().Items.Select(t => t.Text).Should().Contain("Stopped. Progress is kept: resume any time.");
    }

    [Fact]
    public async Task Delete_working_copy_cancels_the_job_and_deletes_its_folder()
    {
        // Arrange
        using var services = Build();
        var tab = await StartedAsync(services);
        var folder = services.GetRequiredService<GenerationService>().FolderOf(GameId.Steam(10))!;
        tab.CancelCommand.Execute(null);
        var dialog = (GenerationCancelDialogViewModel)services.GetRequiredService<IDialogService>().Current!;

        // Act
        dialog.DeleteCommand.Execute(null);

        // Assert
        var toasts = services.GetRequiredService<ToastService>();
        await WaitUntil(() => toasts.Items.Any(t => t.Text == "Generation cancelled, the working copy deleted."));
        (tab.ShowForm, Directory.Exists(folder), services.GetRequiredService<GenerationService>().FindOpen(GameId.Steam(10))).Should().Be((true, false, (GenerationJob?)null));
    }

    [Fact]
    public async Task Back_leaves_the_job_running()
    {
        // Arrange
        using var services = Build();
        var tab = await StartedAsync(services);
        tab.CancelCommand.Execute(null);
        var dialogs = services.GetRequiredService<IDialogService>();
        var dialog = (GenerationCancelDialogViewModel)dialogs.Current!;

        // Act
        dialog.BackCommand.Execute(null);

        // Assert
        (dialogs.Current, tab.IsRunning).Should().Be(((DialogViewModel?)null, true));
    }

    [Fact]
    public async Task Closing_the_cancel_dialog_is_back()
    {
        // Arrange
        using var services = Build();
        var tab = await StartedAsync(services);
        tab.CancelCommand.Execute(null);
        var dialogs = services.GetRequiredService<IDialogService>();

        // Act
        dialogs.Close(dialogs.Current!);

        // Assert
        (tab.IsRunning, services.GetRequiredService<ToastService>().Items.Count).Should().Be((true, 0));
    }

    [Fact]
    public void Details_of_a_failed_job_show_the_reason_the_message_and_retry()
    {
        // Arrange
        using var services = Build();
        JobOnDisk(services, j => j with
        {
            Stage = GenerationStage.Build,
            Status = GenerationJobStatus.Failed,
            Failure = new GenerationFailure(GenerationStage.Build, GenerationFailureKind.Toolkit, "Rebuilding data/Map001.json failed"),
        });
        var tab = Open(services);

        // Act
        tab.DetailsCommand.Execute(null);

        // Assert
        var dialog = (GenerationDetailsDialogViewModel)services.GetRequiredService<IDialogService>().Current!;
        (dialog.Title, dialog.Reason, dialog.Message, dialog.CanRetry).Should().Be((
            "Generation log: Alpha", "The toolkit couldn't process the game files. Details have the reason.", "Rebuilding data/Map001.json failed", true));
    }
}
