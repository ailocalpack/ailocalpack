using System.Globalization;
using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Pipeline;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Generation;

/// <summary>Makes the fix runner of a game's last job: the real one (<see cref="PipelineStagesFactory"/>), or fakes in tests.</summary>
public interface IFixRunnerFactory
{
    /// <exception cref="InvalidOperationException">The agent is unknown, or the app's own path is.</exception>
    IFixRunner Create(Game game, GenerationJobEntry entry);
}

/// <summary>A draft of the result as the Drafts menu shows it.</summary>
/// <param name="Report">Its build report (test run, tried, crash); <c>null</c> when its copy is unreadable.</param>
public sealed record FixDraftView(int Number, DateTimeOffset Created, bool IsCurrent, LocalPackReport? Report);

/// <summary>The fix conversation of a game's result, as the Generation tab shows it.</summary>
/// <param name="Running">The agent turn being made (its ID), or <c>null</c>.</param>
/// <param name="Steps">The running turn's steps so far.</param>
/// <param name="Problem">Why the last action didn't happen (busy, disk), in English for the log; the UI words <paramref name="ProblemKind"/>.</param>
public sealed record FixView(
    GenerationJobEntry Entry,
    LocalPack Pack,
    FixState State,
    IReadOnlyList<FixDraftView> Drafts,
    int? Running,
    IReadOnlyList<string> Steps,
    FixProblem? ProblemKind = null,
    string? Problem = null)
{
    public bool IsRunning => Running is not null;
}

/// <summary>
/// Fixes with AI in the app (task 75, design §6e): sends the player's requests about a game's result (the latest
/// completed job and its local pack, <see cref="GenerationService.ResultOf"/>) to the job's agent in the background,
/// one at a time per game, and streams the steps; applies or drops a plan; goes back to an earlier draft. A running fix
/// is an Activity task. When the game closes right after it started with the result's pack installed
/// (<see cref="LocalPacks.Crashed"/>), an Activity decision offers a fix; it is settled once a request is sent. The
/// attachments of a crash: the session, the game's log written since it started (<see cref="GameLogs"/>, copied into
/// the job's <c>fix/attachments/</c>) and the draft's changes. <see cref="Changed"/> tells the pages (any thread).
/// Disposing it stops the running fixes (the job is put back on the next start: <see cref="FixRunner.Recover"/>).
/// </summary>
public sealed class FixService : IDisposable
{
    private readonly GenerationService _jobs;
    private readonly IFixRunnerFactory _factory;
    private readonly LocalPacks _localPacks;
    private readonly LocalPackStore _store;
    private readonly ActivityStore _activity;
    private readonly OsEnvironment _environment;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<GameId, Running> _running = [];
    private readonly Dictionary<GameId, (FixProblem Kind, string Message)> _problems = [];
    private readonly HashSet<string> _recovered = [];
    private readonly CancellationTokenSource _quit = new();

    public FixService(
        GenerationService jobs,
        IFixRunnerFactory factory,
        LocalPacks localPacks,
        LocalPackStore store,
        ActivityStore activity,
        OsEnvironment environment,
        Localizer localizer,
        TimeProvider time,
        ILogger<FixService>? logger = null)
    {
        _jobs = jobs;
        _factory = factory;
        _localPacks = localPacks;
        _store = store;
        _activity = activity;
        _environment = environment;
        _localizer = localizer;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _localPacks.Crashed += OnCrashed;
    }

    /// <summary>A game's fix conversation changed (a turn, a step, a draft); raised on any thread.</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>The game's fix conversation, or <c>null</c> without a result. Reads the disk.</summary>
    public FixView? Of(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_jobs.ResultOf(game) is not { } result)
        {
            return null;
        }

        var entry = new GenerationJobEntry(result.Folder, result.Job);
        Running? running;
        (FixProblem Kind, string Message)? problem;
        lock (_lock)
        {
            running = _running.GetValueOrDefault(game);
            problem = _problems.TryGetValue(game, out var p) ? p : null;
        }

        if (running is null)
        {
            Recover(entry);
        }

        var fix = new FixFiles(entry.Folder);
        var state = fix.Read();
        var pack = _localPacks.FindByJob(game, result.Job.Id) ?? result.Pack;
        var current = pack.Report.DraftNumber;
        var drafts = state.Drafts.Select(d => d.Number).Append(current).Distinct().Order()
            .Select(n => new FixDraftView(
                n,
                state.Drafts.FirstOrDefault(d => d.Number == n)?.Created ?? pack.Report.Created,
                n == current,
                n == current ? pack.Report : LocalPackStore.SnapshotReportOf(fix.DraftPackFolder(n))))
            .ToList();
        IReadOnlyList<string> steps;
        lock (_lock)
        {
            steps = running is null ? [] : [.. running.Steps];
        }

        return new FixView(entry, pack, state, drafts, running?.Turn, steps, problem?.Kind, problem?.Message);
    }

    /// <summary>A fix runs for the game here.</summary>
    public bool IsRunning(GameId game)
    {
        lock (_lock)
        {
            return _running.ContainsKey(game);
        }
    }

    /// <summary>The attachments a crash brings: the session, the game's log since it started (when found) and the draft's changes.</summary>
    public IReadOnlyList<FixAttachment> CrashAttachments(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Of(game.Id) is not { } view)
        {
            return [];
        }

        var list = new List<FixAttachment>();
        if (view.Pack.Report.Crash is { } crash)
        {
            list.Add(FixAttachment.Session(crash.Lasted, crash.ExitCode));
            var engine = EngineKind.TryParse(game.Engine, out var kind) ? kind.Engine : GameEngine.Unknown;
            if (GameLogs.Find(game.InstallPath, engine, _environment, crash.At - crash.Lasted) is { } log)
            {
                try
                {
                    list.Add(new FixFiles(view.Entry.Folder).AddLog(log.Name, log.Lines));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(e, "Couldn't keep the game log {Log} for a fix", log.Path);
                }
            }
        }

        list.Add(FixAttachment.Changes());
        return list;
    }

    /// <summary>Copies a screenshot the player picked into the job's attachments; <c>null</c> when it can't be read.</summary>
    public FixAttachment? AddScreenshot(GameId game, string path)
    {
        if (Of(game) is not { } view)
        {
            return null;
        }

        try
        {
            return new FixFiles(view.Entry.Folder).AddScreenshot(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(e, "Couldn't add the screenshot {Path}", path);
            return null;
        }
    }

    /// <summary>The game's log written since the last session started, kept for the request; <c>null</c> when none is found.</summary>
    public FixAttachment? AddGameLog(Game game, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Of(game.Id) is not { } view)
        {
            return null;
        }

        var engine = EngineKind.TryParse(game.Engine, out var kind) ? kind.Engine : GameEngine.Unknown;
        if (GameLogs.Find(game.InstallPath, engine, _environment, since) is not { } log)
        {
            return null;
        }

        try
        {
            return new FixFiles(view.Entry.Folder).AddLog(log.Name, log.Lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't keep the game log {Log} for a fix", log.Path);
            return null;
        }
    }

    /// <summary>Sends a request; returns once the agent's turn is made (or it failed). Never throws.</summary>
    public Task<bool> SendAsync(Game game, string text, IReadOnlyList<FixAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(game);
        return RunAsync(game, "send", (runner, entry, progress, token) => runner.SendAsync(entry, new FixRequest(text, attachments), progress, token), resolveCrash: true);
    }

    /// <summary><c>Apply the plan</c>: builds the next draft from the plan's changes. Never throws.</summary>
    public Task<bool> ApplyPlanAsync(Game game, int turn)
    {
        ArgumentNullException.ThrowIfNull(game);
        return RunAsync(game, "apply", (runner, entry, progress, token) => runner.ApplyPlanAsync(entry, turn, progress, token), resolveCrash: false);
    }

    /// <summary><c>Change the request</c>: drops the plan. Never throws.</summary>
    public void DropPlan(Game game, int turn)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsRunning(game.Id) || _jobs.ResultOf(game.Id) is not { } result)
        {
            return;
        }

        try
        {
            _factory.Create(game, new GenerationJobEntry(result.Folder, result.Job)).DropPlan(new GenerationJobEntry(result.Folder, result.Job), turn);
            SetProblem(game.Id, null);
        }
        catch (Exception e) when (e is FixException or InvalidOperationException)
        {
            Fail(game.Id, e);
        }

        Changed?.Invoke(this, game.Id);
    }

    /// <summary><c>Go back to it</c>: makes draft <paramref name="number"/> current again. Never throws.</summary>
    public bool GoBack(GameId game, int number)
    {
        if (IsRunning(game) || _jobs.ResultOf(game) is not { } result)
        {
            return false;
        }

        try
        {
            FixRunner.GoBack(new GenerationJobEntry(result.Folder, result.Job), number, _store, _time);
            SetProblem(game, null);
            _logger.LogInformation("Fix: {Game} went back to draft {Draft}", game, number);
        }
        catch (FixException e)
        {
            Fail(game, e);
            Changed?.Invoke(this, game);
            return false;
        }

        _localPacks.Refresh(game);
        Changed?.Invoke(this, game);
        return true;
    }

    /// <summary><c>Stop</c>: the running fix ends, nothing changes.</summary>
    public void Stop(GameId game)
    {
        lock (_lock)
        {
            _running.GetValueOrDefault(game)?.Cancel.Cancel();
        }
    }

    public void Dispose()
    {
        _localPacks.Crashed -= OnCrashed;
        _quit.Cancel();
        _quit.Dispose();
    }

    /// <summary>Runs one fix action in the background; <c>false</c> when it couldn't start (busy, no result, the agent unknown, disk).</summary>
    private async Task<bool> RunAsync(Game game, string what, Func<IFixRunner, GenerationJobEntry, IProgress<FixProgress>, CancellationToken, Task<FixTurn>> run, bool resolveCrash)
    {
        if (_jobs.ResultOf(game.Id) is not { } result)
        {
            return false;
        }

        var entry = new GenerationJobEntry(result.Folder, result.Job);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_quit.Token);
        var running = new Running(cancel);
        lock (_lock)
        {
            if (!_running.TryAdd(game.Id, running))
            {
                cancel.Dispose();
                return false;
            }

            _problems.Remove(game.Id);
        }


        var taskId = $"fix:{result.Job.Id}";
        _activity.SetTask(new ActivityTask(taskId, ActivityTaskKind.Generation, game.Id, game.Name,
            _localizer.Format(nameof(Strings.Fix_Activity_Task), LanguageNames.Native(result.Job.Language)), null, null, _time.GetUtcNow())
        {
            Cancel = () => Stop(game.Id),
        });
        Changed?.Invoke(this, game.Id);
        var progress = new SyncProgress(p =>
        {
            lock (_lock)
            {
                running.Turn = p.Turn;
                running.Steps = p.Steps;
            }

            Changed?.Invoke(this, game.Id);
        });
        var started = false;
        try
        {
            var runner = _factory.Create(game, entry);
            started = true;
            if (resolveCrash)
            {
                _activity.ResolveGame(game.Id, e => e.Kind == ActivityEventKind.TranslationCrashed);
            }

            var turn = await Task.Run(() => run(runner, entry, progress, cancel.Token), cancel.Token).ConfigureAwait(false);
            _logger.LogInformation("Fix ({What}) of {Game}: {Outcome}", what, game.Id, turn.Outcome);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Fix ({What}) of {Game} stopped", what, game.Id);
        }
        catch (Exception e) when (e is FixException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Fix ({What}) of {Game} didn't run", what, game.Id);
            Fail(game.Id, e);
            started = started && e is not FixException { Problem: FixProblem.Busy or FixProblem.BadRequest or FixProblem.NoPack };
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(game.Id);
            }

            cancel.Dispose();
            _activity.EndTask(taskId);
            _localPacks.Refresh(game.Id);
            Changed?.Invoke(this, game.Id);
        }

        return started;
    }

    private void OnCrashed(object? sender, LocalPack pack)
    {
        var game = pack.Manifest.Game;
        if (_jobs.ResultOf(game) is not { } result || result.Job.Id != pack.Report.Job || pack.Report.Crash is not { } crash)
        {
            return;
        }

        _activity.Add(ActivityEvent.Create(ActivityEventKind.TranslationCrashed, crash.At) with
        {
            Game = game,
            GameName = result.Job.GameName,
            PackTitle = pack.Manifest.Title,
            Detail = Math.Max(1, Math.Round(crash.Lasted.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            Key = $"translation-crashed:{result.Job.Id}:{pack.Report.DraftNumber}:{crash.At.ToUnixTimeSeconds()}",
        });
        Changed?.Invoke(this, game);
    }

    private void Recover(GenerationJobEntry entry)
    {
        lock (_lock)
        {
            if (!_recovered.Add(entry.Folder))
            {
                return;
            }
        }

        if (FixRunner.Recover(entry, _time))
        {
            _logger.LogInformation("Fix of {Game} interrupted earlier: the job was put back", entry.Job.Game);
        }
    }

    private void Fail(GameId game, Exception e) =>
        SetProblem(game, (e switch
        {
            FixException f => f.Problem,
            InvalidOperationException => FixProblem.Agent,
            _ => FixProblem.Disk,
        }, e.Message));

    private void SetProblem(GameId game, (FixProblem Kind, string Message)? problem)
    {
        lock (_lock)
        {
            if (problem is { } p)
            {
                _problems[game] = p;
            }
            else
            {
                _problems.Remove(game);
            }
        }
    }

    private sealed class Running(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;

        public int? Turn { get; set; }

        public IReadOnlyList<string> Steps { get; set; } = [];
    }

    /// <summary>Reports at once on the reporting thread (no captured context: the UI goes through <see cref="Changed"/>).</summary>
    private sealed class SyncProgress(Action<FixProgress> report) : IProgress<FixProgress>
    {
        public void Report(FixProgress value) => report(value);
    }
}
