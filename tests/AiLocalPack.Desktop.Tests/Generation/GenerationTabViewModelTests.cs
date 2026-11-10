using AiLocalPack.Agents;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The Generation tab's start form (task 52) over the real service graph: fake agents, estimate and stages.</summary>
public sealed class GenerationTabViewModelTests
{
    /// <summary>Steam game 10 "Alpha", target language Russian; <paramref name="arrange"/> sets agents and the estimate first.</summary>
    private static ServiceProvider Build(Action<FakeAgentHost, FakeEstimates>? arrange = null)
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        arrange?.Invoke(services.GetRequiredService<FakeAgentHost>(), services.GetRequiredService<FakeEstimates>());
        return services;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    private static GenerationTabViewModel Open(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation).Generation;

    private static void ClaudeReady(FakeAgentHost agents, FakeEstimates _) => agents.Probes["claude"] = FakeAgentHost.SignedIn("claude");

    [Fact]
    public void Estimate_in_progress_shows_skeletons_and_holds_start()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => new TaskCompletionSource<GenerationSizeEstimate>().Task;
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.IsEstimating, tab.StartBlock, tab.StartBlockText).Should().Be((true, GenerationStartBlock.Estimating, "Estimating the size…"));
    }

    [Fact]
    public void Finished_estimate_fills_the_tiles()
    {
        // Arrange
        var pending = new TaskCompletionSource<GenerationSizeEstimate>();
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => pending.Task;
        });
        var tab = Open(services);

        // Act
        pending.SetResult(FakeEstimates.Of(strings: 12_384, words: 180_412));

        // Assert
        (tab.IsEstimated, tab.StringsValue, tab.StringsDetail).Should().Be((true, "12,384", "strings · 180,412 words"));
    }

    [Fact]
    public void Estimate_from_file_sizes_is_marked_as_a_guess()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(strings: 500, source: TextSizeSource.UnknownEngine, engine: EngineKind.Unknown));
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.StringsValue, tab.StringsDetail).Should().Be(("≈ 500", "rough guess from file sizes"));
    }

    [Fact]
    public void Failed_estimate_says_why_and_holds_start()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => Task.FromException<GenerationSizeEstimate>(new IOException("drive gone"));
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.IsEstimateFailed, tab.EstimateErrorText, tab.StartBlock).Should().Be((true, "Couldn't size up the game: drive gone", GenerationStartBlock.EstimateFailed));
    }

    [Fact]
    public void Retry_after_a_failed_estimate_estimates_again()
    {
        // Arrange
        var fail = true;
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => fail ? Task.FromException<GenerationSizeEstimate>(new IOException("drive gone")) : Task.FromResult(FakeEstimates.Of());
        });
        var tab = Open(services);
        fail = false;

        // Act
        tab.RetryEstimateCommand.Execute(null);

        // Assert
        (tab.IsEstimated, services.GetRequiredService<FakeEstimates>().Calls).Should().Be((true, 2));
    }

    [Fact]
    public void Ready_agent_shows_its_source_and_version()
    {
        // Arrange
        using var services = Build(ClaudeReady);

        // Act
        var card = Open(services).Agents.Single(a => a.AgentId == "claude");

        // Assert
        (card.StatusText, card.IsStatusOk, card.SourceText).Should().Be(("Ready", true, "your copy · v2.1.319"));
    }

    [Fact]
    public void Agent_installed_by_the_app_says_so()
    {
        // Arrange
        using var services = Build((agents, _) => agents.Probes["claude"] = FakeAgentHost.SignedIn("claude", AgentSource.Managed, "2.1.311"));

        // Act
        var card = Open(services).Agents.Single(a => a.AgentId == "claude");

        // Assert
        card.SourceText.Should().Be("installed by ailocalpack · v2.1.311");
    }

    [Fact]
    public void Missing_agent_offers_install()
    {
        // Arrange
        using var services = Build();

        // Act
        var card = Open(services).Agents.Single(a => a.AgentId == "codex");

        // Assert
        (card.StatusText, card.IsStatusBad, card.Row.CanInstall).Should().Be(("Not installed", true, true));
    }

    [Fact]
    public void Signed_out_agent_offers_sign_in()
    {
        // Arrange
        using var services = Build((agents, _) => agents.Probes["claude"] = FakeAgentHost.SignedOut("claude"));

        // Act
        var card = Open(services).Agents.Single(a => a.AgentId == "claude");

        // Assert
        (card.StatusText, card.Row.CanSignIn).Should().Be(("Not signed in", true));
    }

    [Fact]
    public async Task Install_from_the_card_shows_its_progress()
    {
        // Arrange
        var finish = new TaskCompletionSource<AgentInstallResult>();
        using var services = Build((agents, _) => agents.Install = (_, progress, _) =>
        {
            progress?.Report(new AgentInstallProgress(AgentInstallStage.Downloading, 40, 100));
            return finish.Task;
        });
        var card = Open(services).Agents.Single(a => a.AgentId == "claude");

        // Act
        var install = card.Row.InstallCommand.ExecuteAsync(null);

        // Assert
        (card.StatusText, card.HasProgress, card.IsStatusBusy).Should().Be(("Downloading the official build · 40%", true, true));
        finish.SetResult(new AgentInstallResult(null, AgentInstallProblem.Offline, "offline"));
        await install;
    }

    [Fact]
    public async Task Sign_in_from_the_card_waits_for_the_browser()
    {
        // Arrange
        var finish = new TaskCompletionSource<AgentSignInResult>();
        using var services = Build((agents, _) =>
        {
            agents.Probes["claude"] = FakeAgentHost.SignedOut("claude");
            agents.SignIn = (_, _) => finish.Task;
        });
        var card = Open(services).Agents.Single(a => a.AgentId == "claude");

        // Act
        var signIn = card.Row.SignInCommand.ExecuteAsync(null);

        // Assert
        (card.StatusText, card.Row.CanCancel).Should().Be(("Finish signing in in your browser…", true));
        finish.SetResult(new AgentSignInResult(AgentSignInOutcome.Cancelled, null));
        await signIn;
    }

    [Fact]
    public void Ready_agent_is_preselected_over_a_missing_one()
    {
        // Arrange
        using var services = Build((agents, _) => agents.Probes["codex"] = FakeAgentHost.SignedIn("codex"));

        // Act
        var tab = Open(services);

        // Assert
        tab.SelectedAgent!.AgentId.Should().Be("codex");
    }

    [Fact]
    public void Default_agent_from_settings_is_preselected()
    {
        // Arrange
        using var services = Build((agents, _) =>
        {
            agents.Probes["claude"] = FakeAgentHost.SignedIn("claude");
            agents.Probes["codex"] = FakeAgentHost.SignedIn("codex");
        });
        services.GetRequiredService<SettingsStore>().Update(s => s with { DefaultAgent = "codex" });

        // Act
        var tab = Open(services);

        // Assert
        tab.SelectedAgent!.AgentId.Should().Be("codex");
    }

    [Fact]
    public void Choosing_an_agent_that_isnt_ready_holds_start()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var tab = Open(services);

        // Act
        tab.Agents.Single(a => a.AgentId == "codex").SelectCommand.Execute(null);

        // Assert
        (tab.StartBlock, tab.StartBlockText).Should().Be((GenerationStartBlock.AgentNotReady, "Codex isn't ready yet"));
    }

    [Fact]
    public void Ready_agent_and_estimate_turn_start_on()
    {
        // Arrange
        using var services = Build(ClaudeReady);

        // Act
        var tab = Open(services);

        // Assert
        (tab.CanStart, tab.StartCommand.CanExecute(null), tab.Checks.All(c => c.IsOk)).Should().Be((true, true, true));
    }

    [Fact]
    public void Checks_name_the_engine_and_the_disk()
    {
        // Arrange
        using var services = Build(ClaudeReady);

        // Act
        var checks = Open(services).Checks.Select(c => c.Text);

        // Assert
        checks.Should().Equal("Unity (Mono) · supported by the toolkit", "3.0 GB working copy · 120.0 GB free", "Claude Code ready", "No anti-cheat detected");
    }

    [Fact]
    public void Unknown_engine_lets_start_with_a_warning_that_the_agent_writes_its_own_reader()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(source: TextSizeSource.UnknownEngine, engine: EngineKind.Unknown));
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.StartBlock, tab.Checks[0].IsWarning, tab.Checks[0].Text)
            .Should().Be(((GenerationStartBlock?)null, true, "Unknown engine: the agent writes its own reader, and the pack is a binary patch"));
    }

    [Fact]
    public void A_toolkit_that_failed_to_read_the_game_holds_start()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(source: TextSizeSource.ToolkitFailed));
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.StartBlock, tab.Checks[0].IsBad).Should().Be((GenerationStartBlock.EngineUnsupported, true));
    }

    [Fact]
    public void Short_disk_holds_start_and_says_how_much_is_needed()
    {
        // Arrange
        using var services = Build((agents, estimates) =>
        {
            ClaudeReady(agents, estimates);
            estimates.Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(copyBytes: 3 * FakeEstimates.Gb, freeBytes: FakeEstimates.Gb));
        });

        // Act
        var tab = Open(services);

        // Assert
        (tab.StartBlock, tab.Checks[1].Text).Should().Be((GenerationStartBlock.NotEnoughDisk, "Not enough space: 3.1 GB needed, 1.0 GB free"));
    }

    [Fact]
    public async Task Start_creates_the_job_and_switches_to_progress()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var tab = Open(services);
        tab.ReviewGlossary = true;

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        var job = services.GetRequiredService<FakeGenerationStages>().Jobs.Single();
        (tab.ShowProgress, job.Language, job.Agent, job.Options.ReviewGlossary).Should().Be((true, "ru", "claude", true));
    }

    [Fact]
    public async Task Started_job_turns_the_header_action_into_view_progress()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation);

        // Act
        await page.Generation.StartCommand.ExecuteAsync(null);

        // Assert
        page.Action.Kind.Should().Be(GamePrimaryActionKind.ViewProgress);
    }

    [Fact]
    public async Task Start_in_another_language_uses_that_language()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var tab = Open(services);
        tab.LanguageOptions.Single(l => l.Tag == "de").SelectCommand.Execute(null);

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        services.GetRequiredService<FakeGenerationStages>().Jobs.Single().Language.Should().Be("de");
    }

    [Fact]
    public async Task Job_made_meanwhile_elsewhere_is_shown_instead()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var tab = Open(services);
        var (_, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(Alpha(services), "ru", "codex", GenerationOptions.Default);
        jobLock.Dispose();

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        (tab.StartError, tab.ShowProgress, tab.Job?.Agent).Should().Be(("This game already has a generation job.", true, "codex"));
    }

    [Fact]
    public void Open_job_opens_the_tab_on_its_progress()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var (_, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(Alpha(services), "ru", "claude", GenerationOptions.Default);
        jobLock.Dispose();

        // Act
        var tab = Open(services);

        // Assert
        (tab.ShowForm, tab.Steps[0].IsRunning).Should().Be((false, true));
    }

    [Fact]
    public void Start_form_doesnt_estimate_while_a_job_is_open()
    {
        // Arrange
        using var services = Build(ClaudeReady);
        var (_, jobLock) = services.GetRequiredService<GenerationJobStore>().Create(Alpha(services), "ru", "claude", GenerationOptions.Default);
        jobLock.Dispose();

        // Act
        Open(services);

        // Assert
        services.GetRequiredService<FakeEstimates>().Calls.Should().Be(0);
    }

    [Fact]
    public void Agent_time_under_an_hour_is_in_minutes()
    {
        // Arrange
        var localizer = Localization.Localizer.Current;

        // Act
        var text = GenerationTabViewModel.FormatTime(TimeSpan.FromMinutes(35), TimeSpan.FromMinutes(80) - TimeSpan.FromMinutes(25), localizer);

        // Assert
        text.Should().Be("35–55 min");
    }

    [Fact]
    public void Agent_time_of_hours_keeps_one_decimal_under_ten()
    {
        // Arrange
        var localizer = Localization.Localizer.Current;

        // Act
        var text = GenerationTabViewModel.FormatTime(TimeSpan.FromMinutes(35), TimeSpan.FromHours(1.3), localizer);

        // Assert
        text.Should().Be("0.6–1.3 h");
    }

    [Fact]
    public void Range_whose_low_end_rounds_to_nothing_is_an_upper_bound()
    {
        // Arrange
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        // Act
        var text = GenerationTabViewModel.FormatRange(0.02, 0.06, culture);

        // Assert
        text.Should().Be("≤ 0.1");
    }

    [Fact]
    public async Task Start_form_has_no_header_action()
    {
        // Arrange
        using var registry = new Library.TestRegistry();
        registry.Pack(20, "ru", "lanterns").Write();
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: registry.Path);
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        await services.GetRequiredService<AiLocalPack.Desktop.Registry.PackIndex>().RefreshAsync();

        // Act
        var page = services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Alpha(services), GamePageTab.Generation);

        // Assert
        (page.Action.Kind, page.HasPrimaryAction).Should().Be((GamePrimaryActionKind.Generate, false));
    }
}
