using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
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

/// <summary>
/// The game page's Generation tab (design §6; task 52: the start form; the progress card is a stub until task 53). The
/// same frame for every state: the five-step header and one centred card. With no open job the card is the start form:
/// the title with the language popover; the estimate tiles (strings and words, agent time, subscription days) with
/// skeletons until the size is known; the agent cards (the same rows as Settings → AI agents, with their inline
/// Install / Sign in); the checks (toolkit support, disk, agent ready, no anti-cheat); the glossary review checkbox; and
/// <c>Start generation</c>, on only by <see cref="GenerationStartRules"/> with the reason under it otherwise. Starting
/// creates the job (<see cref="GenerationService"/>), and the tab follows it from then on.
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
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closed = new();
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
        _logger = logger;
        Agents = [.. agents.Agents.Select(row => new GenerationAgentCard(row, localizer, Pick))];
        foreach (var card in Agents)
        {
            card.StateChanged += OnAgentStateChanged;
        }

        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        RetryEstimateCommand = new RelayCommand(Estimate, () => EstimateState == EstimateState.Failed);
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

    // ---------- Progress (stub until task 53) ----------

    public int Percent => Job is { } job ? GenerationProgress.Of(job).Percent : 0;

    /// <summary>"42%".</summary>
    public string PercentText => _localizer.Format(nameof(Strings.Game_Tab_Percent), Percent);

    /// <summary>"Translating", "Paused", "Failed: …".</summary>
    public string ProgressTitle => Job switch
    {
        null => string.Empty,
        { Status: GenerationJobStatus.WaitingForReview } => _localizer[nameof(Strings.Gen_Progress_Review)],
        { Status: GenerationJobStatus.Paused } => _localizer[nameof(Strings.Gen_Progress_Paused)],
        { Status: GenerationJobStatus.Interrupted } => _localizer[nameof(Strings.Gen_Progress_Interrupted)],
        { Status: GenerationJobStatus.Failed } => _localizer[nameof(Strings.Gen_Progress_Failed)],
        { Status: GenerationJobStatus.Completed } => _localizer[nameof(Strings.Gen_Progress_Completed)],
        { } job => StageName(job.Stage),
    };

    /// <summary>"Into Русский with Claude Code"; the failure's message for a failed job.</summary>
    public string ProgressDetail => Job switch
    {
        null => string.Empty,
        { Failure: { } failure } => failure.Message,
        { } job => _localizer.Format(nameof(Strings.Gen_Progress_Detail), LanguageNames.Native(job.Language), AgentInfo.Of(job.Agent).Name),
    };

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
        Job = _jobs.FindOpen(game.Id);
        BuildLanguages();
        Steps = BuildSteps();
        if (folderChanged && _activated && ShowForm)
        {
            Estimate();
        }

        RaiseAll();
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
        });
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
        return [.. GenerationSteps.Stages.Select((stage, i) => new GenerationStepViewModel(stage, StepName(stage), states[i], i == states.Count - 1))];
    }

    private string StepName(GenerationStage stage) => _localizer[stage switch
    {
        GenerationStage.Setup => nameof(Strings.Gen_Step_Setup),
        GenerationStage.Extract => nameof(Strings.Gen_Step_Extract),
        GenerationStage.Glossary => nameof(Strings.Gen_Step_Glossary),
        GenerationStage.Translate => nameof(Strings.Gen_Step_Translate),
        _ => nameof(Strings.Gen_Step_Build),
    }];

    private string StageName(GenerationStage stage) => _localizer[stage switch
    {
        GenerationStage.Setup => nameof(Strings.Gen_Stage_Setup),
        GenerationStage.Extract => nameof(Strings.Gen_Stage_Extract),
        GenerationStage.Glossary => nameof(Strings.Gen_Stage_Glossary),
        GenerationStage.Translate => nameof(Strings.Gen_Stage_Translate),
        _ => nameof(Strings.Gen_Stage_Build),
    }];

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
    }

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
