using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using AiLocalPack.Pipeline;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>One step of the five-step header: name, state and its icon.</summary>
public sealed class GenerationStepViewModel(GenerationStage stage, string name, GenerationStepState state, bool isLast) : ObservableObject
{
    public GenerationStage Stage { get; } = stage;

    public string Name { get; } = name;

    public GenerationStepState State { get; } = state;

    /// <summary>The line to the next step (none after the last); green once this step is done.</summary>
    public bool HasLine { get; } = !isLast;

    public bool IsDone => State == GenerationStepState.Done;

    public bool IsRunning => State == GenerationStepState.Running;

    public bool IsCurrent => State is GenerationStepState.Current or GenerationStepState.Waiting;

    public bool IsPaused => State == GenerationStepState.Paused;

    public bool IsFailed => State == GenerationStepState.Failed;

    /// <summary>The name is in the text colour once the step is reached.</summary>
    public bool IsReached => State != GenerationStepState.Upcoming;

    /// <summary>Resource key of the icon: the stage's own while upcoming (and on the form), else the state's.</summary>
    public string Icon => State switch
    {
        GenerationStepState.Running => "IconLoader",
        GenerationStepState.Done => "IconCheck",
        GenerationStepState.Waiting => "IconPencil",
        GenerationStepState.Paused => "IconPause",
        GenerationStepState.Failed => "IconX",
        _ => Stage switch
        {
            GenerationStage.Setup => "IconBot",
            GenerationStage.Extract => "IconFile",
            GenerationStage.Glossary => "IconTag",
            GenerationStage.Translate => "IconGlobe",
            _ => "IconPlay",
        },
    };
}

/// <summary>A line of the form's checks: ✓ / ✗ / ⟳ and its text.</summary>
public sealed class GenerationCheckViewModel(GenerationCheckState state, string text) : ObservableObject
{
    public GenerationCheckState State { get; } = state;

    public string Text { get; } = text;

    public bool IsOk => State == GenerationCheckState.Ok;

    public bool IsBad => State == GenerationCheckState.Bad;

    public bool IsPending => State == GenerationCheckState.Pending;
}

public enum GenerationCheckState
{
    Pending,
    Ok,
    Bad,
}

/// <summary>A line of "Just translated": key · original (from the player's copy) · translation, ⚠ with the warning's rule.</summary>
public sealed record GenerationRecentRow(string Key, string Original, string Translation, string? Warning)
{
    public bool HasWarning => Warning is not null;
}

/// <summary>A line of the validator's list: key, rule, original → translation.</summary>
public sealed record GenerationWarningRow(string Key, string Rule, string Original, string Translation);

/// <summary>The banner above the progress card (design §6b states).</summary>
public enum GenerationBanner
{
    None,

    /// <summary>Paused by the agent's limit (teal): resumes by itself, <c>Resume now</c>.</summary>
    Limit,

    /// <summary>Paused by the player (teal): <c>Resume</c>.</summary>
    Paused,

    /// <summary>The app closed during the job (amber): <c>Resume</c> from the stage and chunk.</summary>
    Interrupted,

    /// <summary>A stage failed (red): the reason, <c>Details</c>, <c>Retry stage</c>.</summary>
    Failed,

    /// <summary>The glossary waits for the player (amber): <c>Continue to translation</c> (the review itself is task 54).</summary>
    Review,
}

/// <summary>
/// The game page's Generation tab (design §6; task 52: the start form; task 53: the progress card). The same frame for
/// every state: the five-step header and one centred card. With no open job the card is the start form:
/// the title with the language popover; the estimate tiles (strings and words, agent time, subscription days) with
/// skeletons until the size is known; the agent cards (the same rows as Settings → AI agents, with their inline
/// Install / Sign in); the checks (toolkit support, disk, agent ready, no anti-cheat); the glossary review checkbox; and
/// <c>Start generation</c>, on only by <see cref="GenerationStartRules"/> with the reason under it otherwise. Starting
/// creates the job (<see cref="GenerationService"/>), and the tab follows it from then on: the progress card (big %, the
/// stage with its counter and ETA, Pause / Resume / Retry / Cancel, a thick bar; the strings just translated; the Build
/// step with the font swap; the validator box with its list; the agent's limit; the agent activity log) under the state's
/// banner (paused by the limit or the player, interrupted, failed, waiting for review). <c>progress.json</c> and the log are
/// read again after every change and every <see cref="DetailsInterval"/> while the job runs.
/// </summary>
public sealed partial class GenerationTabViewModel : ObservableObject
{
    private readonly GenerationService _jobs;
    private readonly IGenerationEstimates _estimates;
    private readonly AgentsSettingsSection _agents;
    private readonly SettingsStore _settings;
    private readonly PackIndex _packIndex;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closed = new();
    private GenerationJobDetails _details = GenerationJobDetails.Empty;
    private int _detailsLoop;
    private bool _acting;
    private List<PackLanguageOption> _languages = [];
    private Game _game;
    private string _target;
    private bool _activated;
    private bool _agentPicked;
    private int _estimateRun;
    private GenerationSizeEstimate? _estimate;
    private string? _estimateError;

    [ObservableProperty]
    private string _languageQuery = string.Empty;

    [ObservableProperty]
    private bool _reviewGlossary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartError))]
    private string _startError = string.Empty;

    [ObservableProperty]
    private EstimateState _estimateState = EstimateState.Estimating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    private string _actionError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningsToggleText))]
    private bool _isWarningsOpen;

    [ObservableProperty]
    private bool _isLogOpen;

    private bool _starting;

    public GenerationTabViewModel(
        Game game,
        string language,
        GenerationService jobs,
        IGenerationEstimates estimates,
        AgentsSettingsSection agents,
        SettingsStore settings,
        PackIndex packIndex,
        Localizer localizer,
        IUiDispatcher ui,
        IDialogService dialogs,
        ToastService toasts,
        TimeProvider time,
        ILogger logger)
    {
        _game = game;
        _target = language;
        Language = language;
        _jobs = jobs;
        _estimates = estimates;
        _agents = agents;
        _settings = settings;
        _packIndex = packIndex;
        _localizer = localizer;
        _ui = ui;
        _dialogs = dialogs;
        _toasts = toasts;
        _time = time;
        _logger = logger;
        Agents = [.. agents.Agents.Select(row => new GenerationAgentCard(row, localizer, Pick))];
        foreach (var card in Agents)
        {
            card.StateChanged += OnAgentStateChanged;
        }

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        RetryEstimateCommand = new RelayCommand(Estimate, () => EstimateState == EstimateState.Failed);
        PauseCommand = new RelayCommand(Pause, () => IsRunning && !_acting);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        CancelCommand = new RelayCommand(AskCancel, () => Job is not null && !_acting);
        DetailsCommand = new RelayCommand(ShowDetails, () => Job is not null);
        ToggleWarningsCommand = new RelayCommand(() => IsWarningsOpen = !IsWarningsOpen);
        ToggleLogCommand = new RelayCommand(() => IsLogOpen = !IsLogOpen);
        Job = jobs.FindOpen(game.Id);
        SelectDefaultAgent();
        BuildLanguages();
        Steps = BuildSteps();
    }

    // ---------- Frame ----------

    /// <summary>The game's open job (the progress card), or <c>null</c> (the start form).</summary>
    public GenerationJob? Job { get; private set; }

    public bool ShowForm => Job is null;

    public bool ShowProgress => Job is not null;

    /// <summary>Setup + the four pipeline stages.</summary>
    public IReadOnlyList<GenerationStepViewModel> Steps { get; private set; }

    // ---------- Title and language ----------

    /// <summary>The language the pack will be in (exact tag); the page's target until the player picks another.</summary>
    public string Language { get; private set; }

    /// <summary>"Русский".</summary>
    public string LanguageName => LanguageNames.Native(Language);

    /// <summary>The popover's languages matching <see cref="LanguageQuery"/>: those with packs of this game first, then by name.</summary>
    public IReadOnlyList<PackLanguageOption> LanguageOptions =>
        [.. _languages.Where(l => LanguageQuery.Trim().Length == 0
            || TextSearch.Matches(l.Name, LanguageQuery.Trim())
            || TextSearch.Matches(l.EnglishName, LanguageQuery.Trim())
            || TextSearch.Matches(l.Tag, LanguageQuery.Trim()))];

    public bool HasNoLanguageMatch => LanguageOptions.Count == 0;

    // ---------- Estimate tiles ----------

    public bool IsEstimating => EstimateState == EstimateState.Estimating;

    public bool IsEstimated => EstimateState == EstimateState.Done;

    public bool IsEstimateFailed => EstimateState == EstimateState.Failed;

    /// <summary>The estimate is a guess from file sizes (the toolkit didn't read the strings): numbers get "≈".</summary>
    private bool IsGuess => _estimate is { Work.Basis: EstimateBasis.FileSizes };

    private string Approx => IsGuess ? "≈ " : string.Empty;

    /// <summary>"12,384".</summary>
    public string StringsValue => _estimate is { } e ? Approx + e.Work.Text.Strings.ToString("N0", _localizer.Culture) : "—";

    /// <summary>"strings · 180,412 words", or "rough guess from file sizes".</summary>
    public string StringsDetail => _estimate is not { } e ? string.Empty
        : IsGuess ? _localizer[nameof(Strings.Gen_Tile_Guess)]
        : _localizer.Plural(nameof(Strings.Gen_Tile_Words_One), e.Work.Text.Words, e.Work.Text.Words.ToString("N0", _localizer.Culture));

    /// <summary>"3–5 h", "35–80 min".</summary>
    public string TimeValue => _estimate is { } e ? Approx + FormatTime(e.Work.TimeLow, e.Work.TimeHigh, _localizer) : "—";

    /// <summary>"1–2" days of an entry-level plan's limits.</summary>
    public string LimitsValue => _estimate is { } e ? Approx + FormatRange(e.Work.LimitDaysLow, e.Work.LimitDaysHigh, _localizer.Culture) : "—";

    /// <summary>Why the size couldn't be estimated.</summary>
    public string EstimateErrorText => _estimateError is { } error ? _localizer.Format(nameof(Strings.Gen_Estimate_Failed), error) : string.Empty;

    public IRelayCommand RetryEstimateCommand { get; }

    // ---------- Agents ----------

    public IReadOnlyList<GenerationAgentCard> Agents { get; }

    public GenerationAgentCard? SelectedAgent => Agents.FirstOrDefault(a => a.IsSelected);

    // ---------- Checks ----------

    public IReadOnlyList<GenerationCheckViewModel> Checks => [EngineCheck(), DiskCheck(), AgentCheck(), new(GenerationCheckState.Ok, _localizer[nameof(Strings.Gen_Check_NoAntiCheat)])];

    // ---------- Start ----------

    /// <summary>Why <c>Start generation</c> is off, or <c>null</c>.</summary>
    public GenerationStartBlock? StartBlock => GenerationStartRules.Resolve(new GenerationStartInputs(
        _game.IsAvailable,
        SelectedAgent?.IsReady == true,
        EstimateState,
        _estimate is { Source: TextSizeSource.Toolkit },
        _estimate?.Space.IsEnough == true,
        _starting));

    public bool CanStart => StartBlock is null;

    /// <summary>The reason under the button; empty when it is on.</summary>
    public string StartBlockText => StartBlock switch
    {
        GenerationStartBlock.Starting => _localizer[nameof(Strings.Gen_Start_Starting)],
        GenerationStartBlock.GameUnavailable => _localizer[nameof(Strings.Gen_Start_GameUnavailable)],
        GenerationStartBlock.AgentNotReady => _localizer.Format(nameof(Strings.Gen_Start_AgentNotReady), SelectedAgent?.Name),
        GenerationStartBlock.Estimating => _localizer[nameof(Strings.Gen_Start_Estimating)],
        GenerationStartBlock.EstimateFailed => _localizer[nameof(Strings.Gen_Start_EstimateFailed)],
        GenerationStartBlock.EngineUnsupported => _localizer[nameof(Strings.Gen_Start_EngineUnsupported)],
        GenerationStartBlock.NotEnoughDisk => _localizer[nameof(Strings.Gen_Start_NotEnoughDisk)],
        _ => string.Empty,
    };

    public bool HasStartBlock => StartBlock is not null;

    public bool HasStartError => StartError.Length > 0;

    public IAsyncRelayCommand StartCommand { get; }

    // ---------- Progress (task 53) ----------

    /// <summary>How often the card reads <c>progress.json</c> and the log again while the job runs.</summary>
    public static readonly TimeSpan DetailsInterval = TimeSpan.FromSeconds(2);

    /// <summary>The longest validator list shown inline.</summary>
    public const int MaxWarningRows = 200;

    public bool IsRunning => Job?.Status == GenerationJobStatus.Running;

    /// <summary>Resume (paused, interrupted), Retry stage (failed) or Continue (waiting for review) can run now.</summary>
    public bool CanResume => !_acting && Job?.Status is GenerationJobStatus.Paused or GenerationJobStatus.Interrupted
        or GenerationJobStatus.Failed or GenerationJobStatus.WaitingForReview;

    public int Percent => Job is { } job ? GenerationProgress.Of(job).Percent : 0;

    /// <summary>"42%".</summary>
    public string PercentText => _localizer.Format(nameof(Strings.Game_Tab_Percent), Percent);

    /// <summary>The bar is striped while the job doesn't run.</summary>
    public bool IsBarPaused => Job is not null && !IsRunning;

    /// <summary>"Translating", "Building the glossary"; "Translation ready" once completed.</summary>
    public string ProgressTitle => Job switch
    {
        null => string.Empty,
        { Stage: GenerationStage.Done } => _localizer[nameof(Strings.Gen_Progress_Completed)],
        { } job => GenerationTexts.StageName(job.Stage, _localizer),
    };

    /// <summary>"Chunk 327 of 800 · ≈ 2 h 10 min left" (the ETA only while it runs); the stage's line otherwise.</summary>
    public string ProgressDetail
    {
        get
        {
            if (Job is not { } job)
            {
                return string.Empty;
            }

            var counters = job.Counters;
            var line = job.Stage switch
            {
                GenerationStage.Translate when counters.Chunks > 0 => _localizer.Format(
                    nameof(Strings.Gen_Progress_Counter_Chunks), Math.Min(counters.ChunksDone + 1, counters.Chunks), counters.Chunks),
                GenerationStage.Setup => _localizer[nameof(Strings.Gen_Progress_Sub_Setup)],
                GenerationStage.Extract => _localizer[nameof(Strings.Gen_Progress_Sub_Extract)],
                GenerationStage.Glossary => _localizer[nameof(Strings.Gen_Progress_Sub_Glossary)],
                GenerationStage.Build => _localizer[nameof(Strings.Gen_Progress_Sub_Build)],
                _ => _localizer.Format(nameof(Strings.Gen_Progress_Detail), LanguageNames.Native(job.Language), AgentName),
            };
            return IsRunning && GenerationProgress.Of(job).Remaining is { } left && left > TimeSpan.Zero
                ? _localizer.Format(nameof(Strings.Gen_Progress_Eta), line, GenerationTexts.Duration(left, _localizer))
                : line;
        }
    }

    /// <summary>The job's agent ("Claude Code").</summary>
    public string AgentName => Job is { } job ? AgentInfo.Of(job.Agent).Name : string.Empty;

    public bool ShowPause => IsRunning;

    public bool ShowResume => Job?.Status is GenerationJobStatus.Paused or GenerationJobStatus.Interrupted;

    public bool ShowRetry => Job?.Status == GenerationJobStatus.Failed;

    public bool ShowContinue => Job?.Status == GenerationJobStatus.WaitingForReview;

    public IRelayCommand PauseCommand { get; }

    /// <summary>Resume, Resume now, Retry stage, Continue to translation: the job goes on (<see cref="GenerationService.ResumeAsync"/>).</summary>
    public IAsyncRelayCommand ResumeCommand { get; }

    /// <summary>Opens the cancel dialog (Keep progress / Delete working copy / Back).</summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>The job's log, the failure's reason and message (and Retry stage when it failed).</summary>
    public IRelayCommand DetailsCommand { get; }

    /// <summary>Why the last Resume / Cancel didn't happen.</summary>
    public bool HasActionError => ActionError.Length > 0;

    // ---------- Banner ----------

    public GenerationBanner Banner => Job switch
    {
        { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit } => GenerationBanner.Limit,
        { Status: GenerationJobStatus.Paused } => GenerationBanner.Paused,
        { Status: GenerationJobStatus.Interrupted } => GenerationBanner.Interrupted,
        { Status: GenerationJobStatus.Failed } => GenerationBanner.Failed,
        { Status: GenerationJobStatus.WaitingForReview } => GenerationBanner.Review,
        _ => GenerationBanner.None,
    };

    public bool HasBanner => Banner != GenerationBanner.None;

    /// <summary>Teal: paused (the limit or the player).</summary>
    public bool IsBannerGen => Banner is GenerationBanner.Limit or GenerationBanner.Paused;

    /// <summary>Amber: interrupted, waiting for review.</summary>
    public bool IsBannerWarn => Banner is GenerationBanner.Interrupted or GenerationBanner.Review;

    /// <summary>Red: a failed stage.</summary>
    public bool IsBannerBad => Banner == GenerationBanner.Failed;

    public string BannerIcon => Banner switch
    {
        GenerationBanner.Limit or GenerationBanner.Paused => "IconPause",
        GenerationBanner.Interrupted => "IconAlert",
        GenerationBanner.Failed => "IconXCircle",
        _ => "IconPencil",
    };

    /// <summary>"Your agent subscription limit was reached.", "Stage Build &amp; test failed."…</summary>
    public string BannerTitle => Banner switch
    {
        GenerationBanner.Limit => _localizer[nameof(Strings.Gen_Banner_Limit)],
        GenerationBanner.Paused => _localizer[nameof(Strings.Gen_Banner_Paused)],
        GenerationBanner.Interrupted => _localizer[nameof(Strings.Gen_Banner_Interrupted)],
        GenerationBanner.Failed => _localizer.Format(nameof(Strings.Gen_Banner_Failed), GenerationTexts.StepName(Job?.Failure?.Stage ?? Job!.Stage, _localizer)),
        GenerationBanner.Review => _localizer[nameof(Strings.Gen_Banner_Review)],
        _ => string.Empty,
    };

    /// <summary>"The job resumes automatically at ~18:00.", "Progress is saved: resume from stage Translate, chunk 312 of 800."…</summary>
    public string BannerText => Banner switch
    {
        GenerationBanner.Limit when Job?.ResumeAt is { } at => _localizer.Format(nameof(Strings.Gen_Banner_LimitAt), GenerationTexts.ClockTime(at, _time, _localizer)),
        GenerationBanner.Limit => _localizer[nameof(Strings.Gen_Banner_LimitSoon)],
        GenerationBanner.Paused => _localizer[nameof(Strings.Gen_Banner_PausedText)],
        GenerationBanner.Interrupted => ResumeFrom(Job!),
        GenerationBanner.Failed => GenerationTexts.Failure(Job!.Failure ?? new GenerationFailure(Job.Stage, GenerationFailureKind.Unexpected, string.Empty), AgentName, _localizer),
        GenerationBanner.Review => _localizer[nameof(Strings.Gen_Banner_ReviewText)],
        _ => string.Empty,
    };

    /// <summary>"Resume now" (limit), "Resume", "Retry stage", "Continue to translation".</summary>
    public string BannerActionText => _localizer[Banner switch
    {
        GenerationBanner.Limit => nameof(Strings.Gen_Action_ResumeNow),
        GenerationBanner.Failed => nameof(Strings.Gen_Action_Retry),
        GenerationBanner.Review => nameof(Strings.Gen_Action_ContinueTranslation),
        _ => nameof(Strings.Gen_Action_Resume),
    }];

    public string BannerActionIcon => Banner switch
    {
        GenerationBanner.Failed => "IconRefresh",
        GenerationBanner.Review => "IconChevronRight",
        _ => "IconPlay",
    };

    /// <summary><c>Details</c> on a failed stage's banner.</summary>
    public bool ShowBannerDetails => Banner == GenerationBanner.Failed;

    // ---------- Just translated ----------

    /// <summary>The Translate stage's last strings (or the last ones before it stopped).</summary>
    public bool ShowRecent => Job?.Stage == GenerationStage.Translate && Recent.Count > 0;

    /// <summary>"Just translated" with a blinking dot while it runs, else "Last translated".</summary>
    public string RecentTitle => _localizer[IsRunning ? nameof(Strings.Gen_Recent_Title) : nameof(Strings.Gen_Recent_TitleStopped)];

    public IReadOnlyList<GenerationRecentRow> Recent => [.. _details.Progress.Recent.Select(r =>
        new GenerationRecentRow(r.Key, r.Original, r.Translation, r.Warning is { } rule ? GenerationTexts.Rule(rule, _localizer) : null))];

    // ---------- Build & test ----------

    public bool ShowBuild => Job?.Stage == GenerationStage.Build;

    /// <summary>"Rebuilding the game files", "Launching the test build"…</summary>
    public string BuildStepText => _localizer[(_details.Progress.Build?.Step ?? BuildStep.Checking) switch
    {
        BuildStep.Rebuilding => nameof(Strings.Gen_Build_Rebuilding),
        BuildStep.Fonts => nameof(Strings.Gen_Build_Fonts),
        BuildStep.TestRun => nameof(Strings.Gen_Build_TestRun),
        BuildStep.Packing => nameof(Strings.Gen_Build_Packing),
        _ => nameof(Strings.Gen_Build_Checking),
    }];

    /// <summary>"Fonts: “Orbitron” lack letters of the translation; Noto Sans (OFL) is used instead."</summary>
    public string BuildFontNote => _details.Progress.Build is { SwappedFonts: { Count: > 0 } fonts, Replacement: { } replacement }
        ? _localizer.Format(nameof(Strings.Gen_Build_FontSwap), string.Join(", ", fonts.Select(f => $"“{f}”")), replacement)
        : string.Empty;

    public bool HasBuildFontNote => BuildFontNote.Length > 0;

    // ---------- Validator ----------

    public int WarningCount => _details.Progress.Warnings.Count;

    public bool HasWarnings => WarningCount > 0;

    /// <summary>"2 validator warnings", "No validator warnings".</summary>
    public string WarningCountText => HasWarnings
        ? _localizer.Plural(nameof(Strings.Gen_Validator_Count_One), WarningCount, WarningCount.ToString("N0", _localizer.Culture))
        : _localizer[nameof(Strings.Gen_Validator_None)];

    public string WarningsToggleText => _localizer[IsWarningsOpen ? nameof(Strings.Gen_Validator_Hide) : nameof(Strings.Gen_Validator_Show)];

    /// <summary>The list under the box (at most <see cref="MaxWarningRows"/>, newest last).</summary>
    public IReadOnlyList<GenerationWarningRow> Warnings => [.. _details.Progress.Warnings.TakeLast(MaxWarningRows)
        .Select(w => new GenerationWarningRow(w.Key, GenerationTexts.Rule(w.Rule, _localizer), w.Original, w.Translation))];

    public bool ShowWarnings => IsWarningsOpen && HasWarnings;

    public IRelayCommand ToggleWarningsCommand { get; }

    // ---------- Agent limit ----------

    /// <summary>"Claude Code limit".</summary>
    public string LimitTitle => _localizer.Format(nameof(Strings.Gen_Limit_Title), AgentName);

    /// <summary>Paused by the limit: the box says "reached".</summary>
    public bool IsLimitReached => Banner == GenerationBanner.Limit;

    /// <summary>The agent reported its usage, or the limit stopped the job.</summary>
    public bool HasLimit => IsLimitReached || _details.Progress.Limit is not null;

    /// <summary>"~45% used", "reached".</summary>
    public string LimitValue => IsLimitReached ? _localizer[nameof(Strings.Gen_Limit_Reached)]
        : _details.Progress.Limit is { } limit ? _localizer.Format(nameof(Strings.Gen_Limit_Used), (int)Math.Round(limit.Used * 100))
        : string.Empty;

    public double LimitPercent => IsLimitReached ? 100 : Math.Round((_details.Progress.Limit?.Used ?? 0) * 100);

    /// <summary>"Current 5-hour window, as the agent reports it", "Resets at ~18:00", or that the agent doesn't report it.</summary>
    public string LimitNote => IsLimitReached && Job?.ResumeAt is { } at
        ? _localizer.Format(nameof(Strings.Gen_Limit_Resets), GenerationTexts.ClockTime(at, _time, _localizer))
        : _details.Progress.Limit?.Window switch
        {
            null when !HasLimit => _localizer.Format(nameof(Strings.Gen_Limit_Unknown), AgentName),
            "five_hour" => _localizer[nameof(Strings.Gen_Limit_FiveHour)],
            "seven_day" => _localizer[nameof(Strings.Gen_Limit_SevenDay)],
            _ => _localizer[nameof(Strings.Gen_Limit_Window)],
        };

    // ---------- Agent activity ----------

    /// <summary>The end of the job's log, times in the player's zone.</summary>
    public string LogText => _details.Log.Count == 0
        ? _localizer[nameof(Strings.Gen_Log_Empty)]
        : string.Join(Environment.NewLine, _details.Log.Select(LocalLine));

    public IRelayCommand ToggleLogCommand { get; }

    /// <summary>Reads <c>progress.json</c> and the log of the game's job again (off the UI thread) and shows them.</summary>
    public async Task RefreshDetailsAsync()
    {
        var game = _game.Id;
        var details = await Task.Run(() => _jobs.ReadDetails(game)).ConfigureAwait(false);
        _ui.Post(() =>
        {
            _details = details;
            RaiseDetails();
        });
    }

    /// <summary>Called when the tab is shown: estimates the game once and checks the agents again.</summary>
    public void Activate()
    {
        if (!_activated)
        {
            _activated = true;
            if (ShowForm)
            {
                Estimate();
            }
        }

        if (ShowForm)
        {
            _ = _agents.CheckAllAsync();
        }
        else
        {
            FollowDetails();
        }
    }

    /// <summary>The page's game, target language and job changed (catalog, settings, a job event).</summary>
    public void Update(Game game, string language)
    {
        var folderChanged = game.InstallPath != _game.InstallPath || game.IsAvailable != _game.IsAvailable;
        _game = game;
        if (Language == _target)
        {
            Language = language;
        }

        _target = language;
        var hadJob = Job is not null;
        Job = _jobs.FindOpen(game.Id);
        BuildLanguages();
        Steps = BuildSteps();
        if (_activated && ShowForm && (folderChanged || hadJob))
        {
            // Back to the form (the job was cancelled): the estimate and the agents again.
            Estimate();
            _ = _agents.CheckAllAsync();
        }

        RaiseAll();
        FollowDetails();
    }

    /// <summary>The UI language changed.</summary>
    public void RefreshTexts()
    {
        Steps = BuildSteps();
        foreach (var card in Agents)
        {
            card.RefreshTexts();
        }

        RaiseAll();
    }

    public void Close()
    {
        _closed.Cancel();
        foreach (var card in Agents)
        {
            card.StateChanged -= OnAgentStateChanged;
            card.Close();
        }
    }

    /// <summary>"3–5 h" (hours, one decimal under 10) or "35–80 min" when it is under an hour; one value when both round the same.</summary>
    public static string FormatTime(TimeSpan low, TimeSpan high, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (high < TimeSpan.FromHours(1))
        {
            var range = FormatRange(Math.Max(1, Math.Round(low.TotalMinutes)), Math.Max(1, Math.Round(high.TotalMinutes)), localizer.Culture);
            return localizer.Format(nameof(Strings.Gen_Tile_Minutes), range);
        }

        return localizer.Format(nameof(Strings.Gen_Tile_Hours), FormatRange(low.TotalHours, high.TotalHours, localizer.Culture));
    }

    /// <summary>
    /// "0.6–1.3", "3–5", "12–20": one decimal under 10, whole numbers above; one value when both round the same; "≤ 0.1"
    /// when the low end rounds to nothing.
    /// </summary>
    public static string FormatRange(double low, double high, CultureInfo culture)
    {
        var a = Number(low, culture);
        var b = Number(Math.Max(high, 0.1), culture);
        return low < 0.05 ? $"≤ {b}" : a == b ? a : $"{a}–{b}";

        static string Number(double value, CultureInfo culture) => value < 10
            ? value.ToString("0.#", culture)
            : Math.Round(value).ToString("N0", culture);
    }

    partial void OnLanguageQueryChanged(string value)
    {
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(HasNoLanguageMatch));
    }

    partial void OnEstimateStateChanged(EstimateState value)
    {
        RetryEstimateCommand.NotifyCanExecuteChanged();
        RaiseAll();
    }

    private void Estimate()
    {
        var run = ++_estimateRun;
        _estimate = null;
        _estimateError = null;
        EstimateState = EstimateState.Estimating;
        _ = EstimateAsync(run);
    }

    private async Task EstimateAsync(int run)
    {
        GenerationSizeEstimate? estimate = null;
        string? error = null;
        try
        {
            estimate = await _estimates.EstimateAsync(_game, _closed.Token);
            _logger.LogInformation(
                "Generation estimate for {Game}: {Strings} strings ({Source}), copy {Bytes} bytes, needs {Needed} of {Free} free",
                _game.Id, estimate.Work.Text.Strings, estimate.Source, estimate.Copy.TotalBytes, estimate.Space.NeededBytes, estimate.Space.FreeBytes);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't estimate the generation for {Game}", _game.Id);
            error = e.Message;
        }

        _ui.Post(() =>
        {
            if (run != _estimateRun)
            {
                return;
            }

            _estimate = estimate;
            _estimateError = error;
            EstimateState = estimate is null ? EstimateState.Failed : EstimateState.Done;
        });
    }

    private async Task StartAsync()
    {
        if (!CanStart || SelectedAgent is not { } agent || _estimate is not { } estimate)
        {
            return;
        }

        _starting = true;
        StartError = string.Empty;
        RaiseStart();
        var options = new GenerationOptions(ReviewGlossary);
        var result = await _jobs.StartAsync(_game, Language, agent.AgentId, options, (estimate.Work.TimeLow + estimate.Work.TimeHigh) / 2);
        _ui.Post(() =>
        {
            _starting = false;
            StartError = result.Problem switch
            {
                null => string.Empty,
                GenerationStartProblem.Blocked => _localizer[nameof(Strings.Gen_StartError_Blocked)],
                GenerationStartProblem.AlreadyExists => _localizer[nameof(Strings.Gen_StartError_Exists)],
                GenerationStartProblem.Disk => _localizer.Format(nameof(Strings.Gen_StartError_Disk), result.Detail),
                _ => _localizer.Format(nameof(Strings.Gen_StartError_Unexpected), result.Detail),
            };
            Job = result.Job ?? _jobs.FindOpen(_game.Id);
            Steps = BuildSteps();
            RaiseAll();
            FollowDetails();
        });
    }

    /// <summary>The player pauses the running job (its step runs again on resume).</summary>
    private void Pause()
    {
        if (IsRunning)
        {
            _jobs.Pause(_game.Id);
        }
    }

    /// <summary>Resume, Resume now, Retry stage or Continue: the job goes on; a refusal shows under the controls.</summary>
    public async Task ResumeAsync()
    {
        if (_acting || Job is null)
        {
            return;
        }

        _acting = true;
        ActionError = string.Empty;
        RaiseControls();
        var result = await _jobs.ResumeAsync(_game);
        _ui.Post(() =>
        {
            _acting = false;
            ActionError = ActionProblem(result);
            Job = _jobs.FindOpen(_game.Id);
            Steps = BuildSteps();
            RaiseAll();
            FollowDetails();
        });
    }

    private void AskCancel()
    {
        if (Job is null)
        {
            return;
        }

        _dialogs.Show<GenerationCancelDialogViewModel>(new GenerationCancelRequest(Percent, OnCancelChosen));
    }

    /// <summary>Keep progress pauses a running job (a stopped one stays as it is); Delete working copy cancels and deletes the folder.</summary>
    private void OnCancelChosen(GenerationCancelChoice choice)
    {
        switch (choice)
        {
            case GenerationCancelChoice.KeepProgress:
                if (IsRunning)
                {
                    _jobs.Pause(_game.Id);
                }

                _toasts.Show(new ToastViewModel(ToastTone.Info, _localizer[nameof(Strings.Gen_Toast_Kept)], []));
                break;
            case GenerationCancelChoice.DeleteWorkingCopy:
                _ = DeleteJobAsync();
                break;
        }
    }

    private async Task DeleteJobAsync()
    {
        _acting = true;
        ActionError = string.Empty;
        RaiseControls();
        var result = await _jobs.CancelAsync(_game.Id, deleteFolder: true);
        _ui.Post(() =>
        {
            _acting = false;
            ActionError = ActionProblem(result);
            if (result.Succeeded)
            {
                _toasts.Show(new ToastViewModel(ToastTone.Ok, _localizer[nameof(Strings.Gen_Toast_Deleted)], []));
            }

            Update(_game, _target);
        });
    }

    private void ShowDetails()
    {
        if (Job is not { } job)
        {
            return;
        }

        var failure = job.Status == GenerationJobStatus.Failed ? job.Failure : null;
        _dialogs.Show<GenerationDetailsDialogViewModel>(new GenerationDetailsRequest(
            _game.Name,
            failure is null ? null : GenerationTexts.Failure(failure, AgentName, _localizer),
            failure?.Message,
            _details.Log.Select(LocalLine).ToList(),
            _jobs.FolderOf(_game.Id),
            failure is null ? null : () => _ = ResumeAsync()));
    }

    private string ActionProblem(GenerationActionResult result) => result.Problem switch
    {
        null => string.Empty,
        GenerationActionProblem.Busy => _localizer[nameof(Strings.Gen_ActionError_Busy)],
        GenerationActionProblem.Blocked => _localizer[nameof(Strings.Gen_ActionError_Blocked)],
        GenerationActionProblem.GameUnavailable => _localizer[nameof(Strings.Gen_ActionError_GameUnavailable)],
        GenerationActionProblem.NotFound => _localizer[nameof(Strings.Gen_ActionError_NotFound)],
        GenerationActionProblem.Disk => _localizer.Format(nameof(Strings.Gen_ActionError_Disk), result.Detail),
        _ => _localizer.Format(nameof(Strings.Gen_ActionError_Unexpected), result.Detail),
    };

    /// <summary>"Progress is saved: resume from stage Translate, chunk 312 of 800."</summary>
    private string ResumeFrom(GenerationJob job) => job.Stage == GenerationStage.Translate && job.Counters.Chunks > 0
        ? _localizer.Format(nameof(Strings.Gen_Banner_ResumeChunk), GenerationTexts.StepName(job.Stage, _localizer),
            Math.Min(job.Counters.ChunksDone + 1, job.Counters.Chunks), job.Counters.Chunks)
        : _localizer.Format(nameof(Strings.Gen_Banner_ResumeStage), GenerationTexts.StepName(job.Stage, _localizer));

    /// <summary>"2026-11-09T17:03:12Z Tool call: put_translations" → "18:03:12 Tool call: put_translations".</summary>
    private string LocalLine(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && DateTimeOffset.TryParse(line.AsSpan(0, space), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("T", _localizer.Culture) + line[space..];
        }

        return line;
    }

    /// <summary>
    /// Reads the job's details now and, while it runs, every <see cref="DetailsInterval"/>; one loop at a time, ended by
    /// <see cref="Close"/> or once the job stops (the next change starts it again).
    /// </summary>
    private void FollowDetails()
    {
        if (!_activated || Job is null || _closed.IsCancellationRequested || Interlocked.Exchange(ref _detailsLoop, 1) == 1)
        {
            return;
        }

        _ = DetailsLoopAsync();
    }

    private async Task DetailsLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                await RefreshDetailsAsync();
                if (!_jobs.IsRunning(_game.Id))
                {
                    break;
                }

                await Task.Delay(DetailsInterval, _time, _closed.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _detailsLoop, 0);
        }
    }

    /// <summary>The player chose an agent: it stays chosen whatever the agents' states do.</summary>
    private void Pick(GenerationAgentCard card)
    {
        _agentPicked = true;
        Select(card);
    }

    private void Select(GenerationAgentCard? card)
    {
        foreach (var agent in Agents)
        {
            agent.IsSelected = agent == card;
        }

        OnPropertyChanged(nameof(SelectedAgent));
        RaiseStart();
    }

    /// <summary>The default agent (Settings: the stored one while ready, else the first ready one), else the stored one, else the first.</summary>
    private void SelectDefaultAgent()
    {
        var stored = _settings.Current.DefaultAgent;
        var id = DefaultAgentRow.Resolve(stored, Agents.Where(a => a.IsReady).Select(a => a.AgentId))
            ?? Agents.FirstOrDefault(a => string.Equals(a.AgentId, stored, StringComparison.OrdinalIgnoreCase))?.AgentId
            ?? Agents.FirstOrDefault()?.AgentId;
        Select(Agents.FirstOrDefault(a => a.AgentId == id));
    }

    private void OnAgentStateChanged(object? sender, EventArgs e)
    {
        // Until the player picks one, the choice follows the default agent as the checks come in.
        if (!_agentPicked)
        {
            SelectDefaultAgent();
        }

        RaiseStart();
    }

    private void BuildLanguages()
    {
        var game = _packIndex.Current?.Index?.Find(_game.Id);
        _languages =
        [
            .. GameLanguages.Tags
                .Select(tag => new PackLanguageOption(tag, LanguageNames.Native(tag), game?.PacksIn(tag).Count ?? 0, tag == Language, SelectLanguage)
                {
                    EnglishName = EnglishName(tag),
                })
                .OrderBy(l => l.Name, LanguageNames.Order),
        ];
    }

    private void SelectLanguage(string tag)
    {
        if (!GameLanguages.IsKnown(tag) || tag == Language)
        {
            return;
        }

        Language = tag;
        LanguageQuery = string.Empty;
        BuildLanguages();
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(LanguageName));
        OnPropertyChanged(nameof(LanguageOptions));
    }

    private IReadOnlyList<GenerationStepViewModel> BuildSteps()
    {
        var states = GenerationSteps.Of(Job);
        return [.. GenerationSteps.Stages.Select((stage, i) => new GenerationStepViewModel(stage, GenerationTexts.StepName(stage, _localizer), states[i], i == states.Count - 1))];
    }

    /// <summary>"Unity (Mono) · supported by the toolkit", or why the toolkit can't read the game.</summary>
    private GenerationCheckViewModel EngineCheck()
    {
        if (_estimate is not { } e)
        {
            return new(IsEstimateFailed ? GenerationCheckState.Bad : GenerationCheckState.Pending, _localizer[nameof(Strings.Gen_Check_Engine_Pending)]);
        }

        var engine = e.Engine.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.Game_Chip_EngineUnknown)] : e.Engine.DisplayName;
        return e.Source switch
        {
            TextSizeSource.Toolkit => new(GenerationCheckState.Ok, _localizer.Format(nameof(Strings.Gen_Check_Engine_Supported), engine)),
            TextSizeSource.UnknownEngine => new(GenerationCheckState.Bad, _localizer[nameof(Strings.Gen_Check_Engine_Unknown)]),
            TextSizeSource.EngineDisabled => new(GenerationCheckState.Bad, _localizer.Format(nameof(Strings.Gen_Check_Engine_Disabled), engine)),
            TextSizeSource.NoTool => new(GenerationCheckState.Bad, _localizer.Format(nameof(Strings.Gen_Check_Engine_NoTool), engine)),
            _ => new(GenerationCheckState.Bad, _localizer.Format(nameof(Strings.Gen_Check_Engine_Failed), engine, e.ToolkitError)),
        };
    }

    /// <summary>"3.2 GB working copy · 120 GB free", "cloned: takes no space", or "Not enough space…".</summary>
    private GenerationCheckViewModel DiskCheck()
    {
        if (_estimate is not { } e)
        {
            return new(IsEstimateFailed ? GenerationCheckState.Bad : GenerationCheckState.Pending, _localizer[nameof(Strings.Gen_Check_Disk_Pending)]);
        }

        var copy = AddGameText.FormatSize(_localizer, e.Copy.TotalBytes);
        if (!e.Space.IsEnough)
        {
            return new(GenerationCheckState.Bad, _localizer.Format(
                nameof(Strings.Gen_Check_Disk_Short),
                AddGameText.FormatSize(_localizer, e.Space.NeededBytes),
                AddGameText.FormatSize(_localizer, e.Space.FreeBytes ?? 0)));
        }

        var text = e.Space.CanClone
            ? _localizer.Format(nameof(Strings.Gen_Check_Disk_Clone), copy)
            : _localizer.Format(nameof(Strings.Gen_Check_Disk_Copy), copy);
        if (e.Space.FreeBytes is { } free)
        {
            text = _localizer.Format(nameof(Strings.Gen_Check_Disk_Free), text, AddGameText.FormatSize(_localizer, free));
        }

        return new(GenerationCheckState.Ok, text);
    }

    private GenerationCheckViewModel AgentCheck() => SelectedAgent switch
    {
        { IsReady: true } agent => new(GenerationCheckState.Ok, _localizer.Format(nameof(Strings.Gen_Check_Agent_Ready), agent.Name)),
        { IsStatusBusy: true } agent => new(GenerationCheckState.Pending, _localizer.Format(nameof(Strings.Gen_Check_Agent_Setup), agent.Name)),
        { } agent => new(GenerationCheckState.Bad, _localizer.Format(nameof(Strings.Gen_Check_Agent_Setup), agent.Name)),
        _ => new(GenerationCheckState.Bad, _localizer[nameof(Strings.Gen_Check_Agent_None)]),
    };

    private void RaiseStart()
    {
        OnPropertyChanged(nameof(Checks));
        OnPropertyChanged(nameof(StartBlock));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartBlockText));
        OnPropertyChanged(nameof(HasStartBlock));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(string.Empty);
        StartCommand.NotifyCanExecuteChanged();
        RetryEstimateCommand.NotifyCanExecuteChanged();
        RaiseControls();
    }

    private void RaiseControls()
    {
        OnPropertyChanged(nameof(CanResume));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DetailsCommand.NotifyCanExecuteChanged();
    }

    private void RaiseDetails()
    {
        OnPropertyChanged(nameof(Recent));
        OnPropertyChanged(nameof(ShowRecent));
        OnPropertyChanged(nameof(BuildStepText));
        OnPropertyChanged(nameof(BuildFontNote));
        OnPropertyChanged(nameof(HasBuildFontNote));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(WarningCountText));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(ShowWarnings));
        OnPropertyChanged(nameof(HasLimit));
        OnPropertyChanged(nameof(LimitValue));
        OnPropertyChanged(nameof(LimitPercent));
        OnPropertyChanged(nameof(LimitNote));
        OnPropertyChanged(nameof(LogText));
    }

    partial void OnIsWarningsOpenChanged(bool value) => OnPropertyChanged(nameof(ShowWarnings));

    private static string EnglishName(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }
}
