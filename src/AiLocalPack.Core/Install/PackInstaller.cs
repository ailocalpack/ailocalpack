using System.Globalization;
using System.Text.Json;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Install;

/// <summary>
/// Applies a set of file changes to a game folder as one transaction, and undoes it (task 27). Install: the guard
/// (<see cref="GameBlocks.EnsureAllowed"/>), checks (game reachable and not running, files writable, free space), a
/// journal written before any game file changes, originals copied into <c>&lt;data&gt;/backups/&lt;game folder&gt;/&lt;transaction&gt;/</c>
/// and hash-checked, new files written atomically, every file hashed again, then the record saved in
/// <see cref="InstalledPackStore"/> (the commit point). Any failure puts the game folder back as it was and records
/// nothing. Uninstall puts the originals back, deletes the files the pack added and the backup; a file the game rewrote
/// since (an update) is left alone. Re-apply (task 33) installs over the game's own record: files still as the pack
/// wrote them keep their old originals, others are backed up afresh. One transaction at a time across processes (a lock
/// file); a journal left by a crash is finished by <see cref="Recover"/>: an install is rolled back (or completed when
/// its record was already saved), an uninstall is finished. Methods block: call them off the UI thread.
/// </summary>
public sealed class PackInstaller
{
    private const string FilesFolder = "files";
    private const string BeforeFolder = "before";
    private const string OperationInstall = "install";
    private const string OperationUninstall = "uninstall";
    private const string PhaseBackup = "backup";
    private const string PhaseApply = "apply";
    private const string CopyBackup = "backup";
    private const string CopyBefore = "before";
    private const string TempSuffix = ".ailocalpack";

    private readonly AppPaths _paths;
    private readonly InstalledPackStore _store;
    private readonly AntiCheatPolicy _policy;
    private readonly IProcessList _processes;
    private readonly OsKind _os;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PackInstaller(
        AppPaths paths,
        InstalledPackStore store,
        AntiCheatPolicy policy,
        IProcessList processes,
        OsEnvironment env,
        TimeProvider? time = null,
        ILogger<PackInstaller>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(env);
        _paths = paths;
        _store = store;
        _policy = policy;
        _processes = processes;
        _os = env.Os;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Tests: called before each file of a stage (stage, relative path); an exception thrown here fails the transaction there.</summary>
    internal Action<InstallStage, string>? BeforeFile { get; set; }

    /// <summary>Tests: called after the record is saved, before the journal and old backup go (a crash there).</summary>
    internal Action? AfterCommit { get; set; }

    /// <summary>Tests: <c>false</c> lets a failure escape without rollback, leaving the journal as a crash would.</summary>
    internal bool RollBackOnFailure { get; set; } = true;

    /// <summary>A journal survived (a crash or a failed rollback): <see cref="Recover"/> has work to do. Never throws.</summary>
    public bool HasInterruptedTransaction => File.Exists(_paths.InstallJournal);

    /// <summary>Installs the files for a game that has no pack installed (<see cref="InstallProblem.AlreadyInstalled"/> otherwise).</summary>
    /// <exception cref="GameBlockedException">The game is blocked (anti-cheat, opt-out).</exception>
    public InstallResult Install(InstallRequest request, RegistryIndex? index, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunInstall(request, index, reapply: false, progress, cancellationToken);

    /// <summary>
    /// Installs over the game's installed pack (task 33: a store update wiped it). Originals of files still as the pack
    /// wrote them come from the old backup; files the game rewrote are backed up as the new originals; the old backup
    /// goes once the new record is saved. Files of the old record the request doesn't write stay in the record.
    /// </summary>
    /// <exception cref="GameBlockedException">The game is blocked (anti-cheat, opt-out).</exception>
    public InstallResult Reapply(InstallRequest request, RegistryIndex? index, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunInstall(request, index, reapply: true, progress, cancellationToken);

    /// <summary>
    /// Restores the originals, deletes the files the pack added and the backup, forgets the record. Dependencies no other
    /// pack uses go too, except those in <paramref name="keep"/> (a replacement whose next pack needs them: they stay,
    /// unused, until it is installed). Allowed for blocked games.
    /// </summary>
    public UninstallResult Uninstall(Game game, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<DependencyKey>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        using var gate = TryLock();
        if (gate is null)
        {
            return UninstallResult.Fail(new InstallFailure(InstallProblem.Busy));
        }

        if (RecoverLocked(cancellationToken) is { Outcome: RecoveryOutcome.Failed } stuck)
        {
            return UninstallResult.Fail(new InstallFailure(InstallProblem.InterruptedInstall, stuck.Failure?.File, stuck.Failure?.Detail));
        }

        if (_store.Find(game.Id) is not { } record)
        {
            return UninstallResult.Fail(new InstallFailure(InstallProblem.NotInstalled));
        }

        if (CheckGame(game) is { } refused)
        {
            return UninstallResult.Fail(refused);
        }

        return RunUninstall(game, record, removesPack: true, keep ?? [], progress, cancellationToken);
    }

    /// <summary>
    /// Removes the game's dependencies that no installed pack uses (left by a replacement whose next pack failed), except
    /// those in <paramref name="keep"/>. Nothing to remove → success with nothing done.
    /// </summary>
    public UninstallResult RemoveUnusedDependencies(Game game, IReadOnlyCollection<DependencyKey>? keep = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        using var gate = TryLock();
        if (gate is null)
        {
            return UninstallResult.Fail(new InstallFailure(InstallProblem.Busy));
        }

        if (RecoverLocked(cancellationToken) is { Outcome: RecoveryOutcome.Failed } stuck)
        {
            return UninstallResult.Fail(new InstallFailure(InstallProblem.InterruptedInstall, stuck.Failure?.File, stuck.Failure?.Detail));
        }

        var record = _store.Find(game.Id);
        var (removed, _) = SplitDependencies(game.Id, record, removesPack: false, keep ?? []);
        if (removed.Count == 0)
        {
            return new UninstallResult(null, [], [], [], null) { KeptDependencies = _store.DependenciesOf(game.Id) };
        }

        if (CheckGame(game) is { } refused)
        {
            return UninstallResult.Fail(refused);
        }

        return RunUninstall(game, record, removesPack: false, keep ?? [], null, cancellationToken);
    }

    private UninstallResult RunUninstall(Game game, InstalledPack? record, bool removesPack, IReadOnlyCollection<DependencyKey> keep, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var root = game.InstallPath;
        var (removed, kept) = SplitDependencies(game.Id, record, removesPack, keep);
        IReadOnlyList<IInstalledFiles> parts = removesPack ? [record!, .. removed] : [.. removed];
        try
        {
            foreach (var file in parts.SelectMany(p => p.Files))
            {
                Preflight(file.Path, InstallPaths.Resolve(root, file.Path));
            }

            WriteJournal(new InstallJournalFile(
                1, OperationUninstall, PhaseApply, game.Id.ToString(), root, parts[0].Backup, null, null, [], [],
                RemovesPack: removesPack, KeepDependencies: [.. keep.Select(KeyText)]));
            var (restored, deleted, changed) = RestoreOriginals(game.Id, parts, root, progress, cancellationToken);
            FinishUninstall(game.Id, removesPack ? null : record, kept);
            _logger.LogInformation(
                "Uninstalled {Pack} from {Game}: {Restored} restored, {Removed} removed, {Kept} kept (changed since); dependencies removed: {Dependencies}",
                removesPack ? $"{record!.PackId} rev {record.Revision}" : "unused dependencies", game.Id, restored.Count, deleted.Count, changed.Count,
                string.Join(", ", removed.Select(d => $"{d.Id} {d.Version}")));
            return new UninstallResult(removesPack ? record : null, restored, deleted, changed, null) { RemovedDependencies = removed, KeptDependencies = kept };
        }
        catch (Exception e) when (IsTransactionError(e) && RollBackOnFailure)
        {
            // Restoring is idempotent: the records stay, so retrying the uninstall finishes what is left.
            TryDeleteJournal();
            var failure = Describe(e, filesRestored: false);
            _logger.LogError(e, "Uninstalling from {Game} failed at {File}: {Problem}", game.Id, failure.File, failure.Problem);
            return UninstallResult.Fail(failure);
        }
    }

    /// <summary>
    /// The game's dependencies that go with an uninstall (no installed pack uses them, not kept) and the records that
    /// stay (users updated). With <paramref name="removesPack"/>, the pack leaves every list of users; otherwise only
    /// dependencies that no installed pack uses go.
    /// </summary>
    private (List<InstalledDependency> Removed, List<InstalledDependency> Kept) SplitDependencies(GameId game, InstalledPack? pack, bool removesPack, IReadOnlyCollection<DependencyKey> keep)
    {
        // Users are the game's installed packs (one per game): none once its pack goes.
        string?[] installed = removesPack ? [] : [pack?.PackId];
        List<InstalledDependency> removed = [], kept = [];
        foreach (var dependency in _store.DependenciesOf(game))
        {
            var users = dependency.UsedBy.Where(u => installed.Contains(u)).ToList();
            if (users.Count == 0 && !keep.Contains(new DependencyKey(dependency.Id, dependency.Version)))
            {
                removed.Add(dependency);
            }
            else
            {
                kept.Add(dependency with { UsedBy = users });
            }
        }

        return (removed, kept);
    }

    private static string KeyText(DependencyKey key) => $"{key.Id}@{key.Version}";

    private static DependencyKey? ParseKey(string? text) =>
        text?.Split('@') is [{ Length: > 0 } id, { Length: > 0 } version] ? new DependencyKey(id, version) : null;

    /// <summary>
    /// Finishes a transaction a crash left behind (start-up, and before every transaction): an install is rolled back,
    /// or completed when its record was saved; an uninstall is finished. Never throws for disk state.
    /// </summary>
    public RecoveryResult Recover(CancellationToken cancellationToken = default)
    {
        using var gate = TryLock();
        return gate is null ? new RecoveryResult(RecoveryOutcome.Busy) : RecoverLocked(cancellationToken);
    }

    private InstallResult RunInstall(InstallRequest request, RegistryIndex? index, bool reapply, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        GameBlocks.EnsureAllowed(request.Game, _policy, index, BlockedOperation.Install);
        Validate(request);
        using var gate = TryLock();
        if (gate is null)
        {
            return InstallResult.Fail(new InstallFailure(InstallProblem.Busy));
        }

        if (RecoverLocked(cancellationToken) is { Outcome: RecoveryOutcome.Failed } stuck)
        {
            return InstallResult.Fail(new InstallFailure(InstallProblem.InterruptedInstall, stuck.Failure?.File, stuck.Failure?.Detail));
        }

        var game = request.Game;
        var old = _store.Find(game.Id);
        if (old is not null && !reapply)
        {
            return InstallResult.Fail(new InstallFailure(InstallProblem.AlreadyInstalled));
        }

        if (old is null && reapply)
        {
            return InstallResult.Fail(new InstallFailure(InstallProblem.NotInstalled));
        }

        if (CheckGame(game) is { } refused)
        {
            return InstallResult.Fail(refused);
        }

        var (parts, shared, conflict) = PlanParts(request, old, reapply);
        if (conflict is not null)
        {
            return InstallResult.Fail(conflict);
        }

        InstallJournalFile? journal = null;
        try
        {
            var plan = PlanInstall(request, parts, shared, progress, cancellationToken);
            foreach (var entry in plan.Entries.Where(e => e.Write))
            {
                Preflight(entry.Path, entry.Full);
            }

            CheckFreeSpace(plan, game.InstallPath);
            journal = plan.Journal;
            WriteJournal(journal);
            BackUp(plan, progress, cancellationToken);
            journal = journal with { Phase = PhaseApply };
            WriteJournal(journal);
            Apply(plan, progress, cancellationToken);
            Verify(plan, progress, cancellationToken);
            _store.Commit(game.Id, plan.Record, plan.Dependencies);
            AfterCommit?.Invoke();
            FinishInstall(journal);
            _logger.LogInformation(
                "Installed {Pack} rev {Revision} ({Language}) into {Game}: {Written} files written, backup {Backup}; dependencies {Dependencies}",
                request.PackId, request.Revision, request.Language, game.Id, plan.Entries.Count(e => e.Write), plan.Record.Backup,
                string.Join(", ", request.Dependencies.Select(d => $"{d.Id} {d.Version}{(shared.Contains(d.Id) ? " (shared)" : string.Empty)}")));
            var used = plan.Dependencies.Where(d => d.UsedBy.Contains(request.PackId, StringComparer.Ordinal)).ToList();
            return new InstallResult(plan.Record, null) { Dependencies = used, SharedDependencies = [.. shared] };
        }
        catch (Exception e) when (IsTransactionError(e) && RollBackOnFailure)
        {
            var restored = journal is null || TryRollBack(journal);
            var failure = Describe(e, restored);
            _logger.LogError(e, "Installing {Pack} into {Game} failed at {File}: {Problem}; files restored: {Restored}", request.PackId, game.Id, failure.File, failure.Problem, restored);
            if (e is OperationCanceledException)
            {
                throw;
            }

            return InstallResult.Fail(failure);
        }
    }

    /// <summary>
    /// What the transaction writes, dependencies first and the pack last: a dependency installed in the same version is
    /// shared (nothing written; re-apply writes it again), one in another version is a conflict, as is a file written by
    /// two of them.
    /// </summary>
    private (List<Part> Parts, HashSet<string> Shared, InstallFailure? Conflict) PlanParts(InstallRequest request, InstalledPack? old, bool reapply)
    {
        var installed = _store.DependenciesOf(request.Game.Id);
        var parts = new List<Part>();
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in request.Dependencies)
        {
            var existing = installed.FirstOrDefault(d => d.Id == dependency.Id);
            if (existing is not null && existing.Version != dependency.Version)
            {
                return (parts, shared, new InstallFailure(InstallProblem.DependencyConflict,
                    Detail: $"{existing.Id} {existing.Version} is installed for the game; this pack needs {dependency.Version}."));
            }

            if (existing is not null && !reapply)
            {
                shared.Add(dependency.Id);
                continue;
            }

            if (dependency.Files.Count == 0)
            {
                // Only one already installed in the version can be named without its files (shared).
                throw new ArgumentException($"The dependency '{dependency.Id}' has no files and isn't installed for the game.", nameof(request));
            }

            parts.Add(new Part(dependency.Id, dependency.Version, dependency.Files, dependency.GeneratedFiles, existing, existing));
        }

        parts.Add(new Part(null, null, request.Files, request.GeneratedFiles, old, null));

        // A file is owned by one part: the pack's files and every dependency's (shared ones as installed).
        var owners = new Dictionary<string, string>(PathNames.Comparer);
        var sources = parts.Select(p => (Name: p.DependencyId ?? request.PackId, Paths: p.Files.Select(f => f.Path)))
            .Concat(installed.Where(d => shared.Contains(d.Id)).Select(d => (Name: d.Id, Paths: d.Files.Select(f => f.Path))));
        foreach (var (name, paths) in sources)
        {
            foreach (var path in paths)
            {
                if (owners.TryGetValue(path, out var other) && other != name)
                {
                    return (parts, shared, new InstallFailure(InstallProblem.DependencyConflict, path, $"{path} is written by both {other} and {name}."));
                }

                owners[path] = name;
            }
        }

        return (parts, shared, null);
    }

    private static void Validate(InstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Game);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackId);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Revision, 1);
        ArgumentNullException.ThrowIfNull(request.Files);
        if (request.Files.Count == 0)
        {
            throw new ArgumentException("An install writes at least one file.", nameof(request));
        }

        foreach (var generated in request.GeneratedFiles)
        {
            if (!InstallPaths.IsValid(generated))
            {
                throw new ArgumentException($"'{generated}' is not a relative '/'-separated path.", nameof(request));
            }
        }

        CheckFiles(request.Files);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in request.Dependencies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency.Version);
            if (!ids.Add(dependency.Id))
            {
                throw new ArgumentException($"The dependency '{dependency.Id}' is listed twice.", nameof(request));
            }

            CheckFiles(dependency.Files);
            if (dependency.GeneratedFiles.FirstOrDefault(g => !DependencyWhitelist.IsGeneratedPath(g)) is { } bad)
            {
                throw new ArgumentException($"'{bad}' is not a generated path.", nameof(request));
            }
        }

        static void CheckFiles(IReadOnlyList<InstallFile> files)
        {
            var seen = new HashSet<string>(PathNames.Comparer);
            foreach (var file in files)
            {
                if (!InstallPaths.IsValid(file.Path))
                {
                    throw new ArgumentException($"'{file.Path}' is not a relative '/'-separated path.", nameof(request));
                }

                if (!seen.Add(file.Path))
                {
                    throw new ArgumentException($"'{file.Path}' is listed twice.", nameof(request));
                }
            }
        }
    }

    /// <summary>Game folder reachable, game not running.</summary>
    private InstallFailure? CheckGame(Game game)
    {
        if (!game.IsAvailable || !Directory.Exists(game.InstallPath))
        {
            return new InstallFailure(InstallProblem.GameUnavailable, Detail: game.InstallPath);
        }

        try
        {
            if (GameProcessMatcher.Find(_processes.Snapshot(), PathNames.RealPath(game.InstallPath), _os) is [var process, ..])
            {
                return new InstallFailure(InstallProblem.GameRunning, Detail: process.Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process list is a courtesy check: a locked file is still caught when it is written.
            _logger.LogWarning(e, "Can't list processes to check whether {Game} is running", game.Id);
        }

        return null;
    }

    private InstallPlan PlanInstall(InstallRequest request, List<Part> parts, HashSet<string> shared, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var root = request.Game.InstallPath;
        var transaction = NewTransactionName();
        var entries = new List<PlanEntry>();
        var total = parts.Sum(p => p.Files.Count);
        var done = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var oldBackup = part.Old is null ? null : BackupFolder(request.Game.Id, part.Old.Backup);
            var oldFiles = (part.Old?.Files ?? []).ToDictionary(f => f.Path, PathNames.Comparer);
            foreach (var file in part.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, InstallStage.Checking, done++, total);
                var full = InstallPaths.Resolve(root, file.Path);
                var (installed, size) = Guard(file.Path, file.Source, () =>
                {
                    using var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    return (InstallPaths.Hash(source, cancellationToken), source.Length);
                });
                if (Directory.Exists(full))
                {
                    throw new InstallFileException(file.Path, full, InstallProblem.IoError, new IOException($"{full} is a folder."));
                }

                var current = Guard(file.Path, full, () => InstallPaths.HashOrNull(full, cancellationToken));
                PlanEntry entry;
                if (oldFiles.Remove(file.Path, out var ours) && current == ours.InstalledSha256)
                {
                    // Still the pack's file: its original is in the old backup; a rollback needs a copy of what is there now.
                    entry = new PlanEntry(index, file.Path, full, file.Source, current != installed, current, current == installed ? null : CopyBefore,
                        ours.OriginalSha256, ours.OriginalSha256 is null ? null : InstallPaths.Resolve(Path.Combine(oldBackup!, FilesFolder), file.Path), installed, size);
                }
                else
                {
                    // The game's own file (or none): it is the original.
                    entry = new PlanEntry(index, file.Path, full, file.Source, current != installed, current, current is null ? null : CopyBackup,
                        current, current is null ? null : full, installed, size);
                }

                entries.Add(entry);
            }

            // Files of the old record this request doesn't write: carried into the new record (their originals copied over).
            foreach (var carried in oldFiles.Values)
            {
                entries.Add(new PlanEntry(index, carried.Path, InstallPaths.Resolve(root, carried.Path), null, false, null, null, carried.OriginalSha256,
                    carried.OriginalSha256 is null ? null : InstallPaths.Resolve(Path.Combine(oldBackup!, FilesFolder), carried.Path), carried.InstalledSha256, 0));
            }
        }

        Report(progress, InstallStage.Checking, done, total);

        var seen = new HashSet<string>(PathNames.Comparer);
        var created = parts.Select((_, index) => CreatedFolders(root, entries.Where(e => e.Part == index && e.Write).Select(e => e.Path), seen)).ToList();
        InstalledPack? record = null;
        var replaced = new Dictionary<string, InstalledDependency>(StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var files = entries.Where(e => e.Part == index).Select(e => new InstalledFile(e.Path, e.Original, e.Installed)).ToList();
            var folders = (part.Old?.AddedFolders ?? []).Union(created[index], PathNames.Comparer).ToList();

            // A generated file already there belongs to the game (or to what is being re-applied, which recorded it).
            var generated = (part.Old?.GeneratedFiles ?? []).Union(part.Generated.Where(g => !GeneratedExists(root, g)), PathNames.Comparer).ToList();
            if (part.DependencyId is null)
            {
                record = new InstalledPack(request.Game.Id, request.Language, request.PackId, request.Revision, now, root, transaction, files, folders)
                {
                    GeneratedFiles = generated,
                };
            }
            else
            {
                var users = (part.OldDependency?.UsedBy ?? []).Union([request.PackId], StringComparer.Ordinal).ToList();
                replaced[part.DependencyId] = new InstalledDependency(request.Game.Id, part.DependencyId, part.Version!, users, now, root, transaction, files, folders)
                {
                    GeneratedFiles = generated,
                };
            }
        }

        var dependencies = new List<InstalledDependency>();
        foreach (var existing in _store.DependenciesOf(request.Game.Id))
        {
            dependencies.Add(replaced.Remove(existing.Id, out var updated) ? updated
                : shared.Contains(existing.Id) ? existing with { UsedBy = [.. existing.UsedBy.Union([request.PackId], StringComparer.Ordinal)] }
                : existing);
        }

        dependencies.AddRange(replaced.Values);
        var journal = new InstallJournalFile(
            1,
            OperationInstall,
            PhaseBackup,
            request.Game.Id.ToString(),
            root,
            transaction,
            parts[^1].Old?.Backup,
            InstalledPackStore.ToJson(record!),
            [.. entries.Where(e => e.Write).Select(e => new JournalEntryJson(e.Path, true, e.Before, e.BeforeCopy))],
            [.. created.SelectMany(c => c)]);
        return new InstallPlan(entries, record!, dependencies, journal, BackupFolder(request.Game.Id, transaction));
    }

    /// <summary>A generated path is there already: the file, the folder, or a file matching the pattern.</summary>
    private static bool GeneratedExists(string root, string generated)
    {
        if (generated.EndsWith('/'))
        {
            return Directory.Exists(InstallPaths.Resolve(root, generated[..^1]));
        }

        if (generated.IndexOfAny(['*', '?']) < 0)
        {
            return File.Exists(InstallPaths.Resolve(root, generated));
        }

        return GeneratedMatches(root, generated).Any();
    }

    /// <summary>Files matching a generated pattern (wildcards in the last part only).</summary>
    private static IEnumerable<string> GeneratedMatches(string root, string pattern)
    {
        var slash = pattern.LastIndexOf('/');
        var folder = slash < 0 ? root : InstallPaths.Resolve(root, pattern[..slash]);
        var name = pattern[(slash + 1)..];
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder)
            .Where(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(name, Path.GetFileName(f), ignoreCase: true))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Parents of the written files that don't exist yet, shallowest first.</summary>
    private static List<string> CreatedFolders(string root, IEnumerable<string> paths, HashSet<string> seen)
    {
        var created = new List<string>();
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (seen.Add(folder) && !Directory.Exists(InstallPaths.Resolve(root, folder)))
                {
                    created.Add(folder);
                }
            }
        }

        return created;
    }

    /// <summary>Fails early, before the journal, on a file that can't be written: read-only, in use, no access.</summary>
    private static void Preflight(string path, string full)
    {
        if (!File.Exists(full))
        {
            return;
        }

        if (InstallErrors.IsReadOnly(full))
        {
            throw new InstallFileException(path, full, InstallProblem.ReadOnly, new IOException($"{full} is read-only."));
        }

        Guard(path, full, () =>
        {
            using var probe = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        });
    }

    private void CheckFreeSpace(InstallPlan plan, string gameFolder)
    {
        Directory.CreateDirectory(_paths.Backups);
        var backup = plan.Entries.Where(e => e.OriginalFrom is not null).Sum(e => SizeOf(e.OriginalFrom!))
            + plan.Entries.Where(e => e.Write && e.BeforeCopy == CopyBefore).Sum(e => SizeOf(e.Full));
        var written = plan.Entries.Where(e => e.Write).Sum(e => e.Size);
        var backupDrive = InstallPaths.DriveOf(_paths.Backups);
        var gameDrive = InstallPaths.DriveOf(gameFolder);
        if (backupDrive is null || gameDrive is null)
        {
            return;
        }

        var sameDrive = PathNames.Same(backupDrive.RootDirectory.FullName, gameDrive.RootDirectory.FullName);
        if (sameDrive ? gameDrive.AvailableFreeSpace < backup + written : backupDrive.AvailableFreeSpace < backup || gameDrive.AvailableFreeSpace < written)
        {
            throw new InstallFileException(null, null, InstallProblem.DiskFull,
                new IOException($"Needs {backup + written:N0} bytes; free: {gameDrive.AvailableFreeSpace:N0} on {gameDrive.Name}"
                    + (sameDrive ? string.Empty : $", {backupDrive.AvailableFreeSpace:N0} on {backupDrive.Name}") + "."));
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void BackUp(InstallPlan plan, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var copies = plan.Entries.Where(e => e.OriginalFrom is not null).ToList();
        var befores = plan.Entries.Where(e => e.Write && e.BeforeCopy == CopyBefore).ToList();
        var total = copies.Count + befores.Count;
        var done = 0;
        foreach (var entry in copies)
        {
            Report(progress, InstallStage.BackingUp, done++, total);
            var target = InstallPaths.Resolve(Path.Combine(plan.BackupFolder, FilesFolder), entry.Path);
            var copied = Guard(entry.Path, entry.OriginalFrom!, () =>
            {
                BeforeFile?.Invoke(InstallStage.BackingUp, entry.Path);
                return CopyInto(entry.OriginalFrom!, target, cancellationToken);
            });
            if (copied != entry.Original)
            {
                // From the game: it changed while installing; from the old backup: the backup is damaged.
                var problem = PathNames.Same(entry.OriginalFrom, entry.Full) ? InstallProblem.VerifyFailed : InstallProblem.BackupDamaged;
                throw new InstallFileException(entry.Path, entry.OriginalFrom, problem, new IOException($"{entry.OriginalFrom} doesn't have the expected content."));
            }
        }

        foreach (var entry in befores)
        {
            Report(progress, InstallStage.BackingUp, done++, total);
            var target = InstallPaths.Resolve(Path.Combine(plan.BackupFolder, BeforeFolder), entry.Path);
            if (Guard(entry.Path, entry.Full, () => CopyInto(entry.Full, target, cancellationToken)) != entry.Before)
            {
                throw new InstallFileException(entry.Path, entry.Full, InstallProblem.VerifyFailed, new IOException($"{entry.Full} changed while installing."));
            }
        }

        Report(progress, InstallStage.BackingUp, done, total);
    }

    private static string CopyInto(string source, string target, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"{source} is missing.", source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return InstallPaths.CopyAtomically(source, target, cancellationToken);
    }

    private void Apply(InstallPlan plan, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var root = plan.Record.InstallPath;
        foreach (var folder in plan.Journal.CreatedFolders ?? [])
        {
            var full = InstallPaths.Resolve(root, folder);
            Guard(folder, full, () => Directory.CreateDirectory(full));
        }

        var writes = plan.Entries.Where(e => e.Write).ToList();
        for (var i = 0; i < writes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = writes[i];
            Report(progress, InstallStage.Writing, i, writes.Count);
            var written = Guard(entry.Path, entry.Full, () =>
            {
                BeforeFile?.Invoke(InstallStage.Writing, entry.Path);
                return InstallPaths.CopyAtomically(entry.Source!, entry.Full, cancellationToken);
            });
            if (written != entry.Installed)
            {
                throw new InstallFileException(entry.Path, entry.Full, InstallProblem.VerifyFailed, new IOException($"{entry.Source} changed while installing."));
            }
        }

        Report(progress, InstallStage.Writing, writes.Count, writes.Count);
    }

    private static void Verify(InstallPlan plan, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var files = plan.Entries.Where(e => e.Source is not null).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            Report(progress, InstallStage.Verifying, i, files.Count);
            var entry = files[i];
            if (Guard(entry.Path, entry.Full, () => InstallPaths.HashOrNull(entry.Full, cancellationToken)) != entry.Installed)
            {
                throw new InstallFileException(entry.Path, entry.Full, InstallProblem.VerifyFailed, new IOException($"{entry.Full} doesn't have the content written."));
            }
        }

        Report(progress, InstallStage.Verifying, files.Count, files.Count);
    }

    /// <summary>After the records are saved: backups no record uses any more, the rollback copies and the journal go (best effort).</summary>
    private void FinishInstall(InstallJournalFile journal)
    {
        var game = GameId.Parse(journal.Game!);
        TryDeleteTree(Path.Combine(BackupFolder(game, journal.Transaction!), BeforeFolder));
        DeleteUnusedBackups(game);
        TryDeleteJournal();
    }

    /// <summary>The records after an uninstall are saved (<paramref name="pack"/> = the pack that stays, if any), unused backups deleted.</summary>
    private void FinishUninstall(GameId game, InstalledPack? pack, IReadOnlyList<InstalledDependency> kept)
    {
        _store.Commit(game, pack, kept);
        DeleteUnusedBackups(game);
        TryDeleteJournal();
    }

    /// <summary>
    /// Deletes the game's backup folders no record points at (one transaction's backup is shared by the pack and the
    /// dependencies it installed, so a dependency kept after its pack keeps the folder). Runs under the lock.
    /// </summary>
    private void DeleteUnusedBackups(GameId game)
    {
        var folder = Path.Combine(_paths.Backups, RegistryLayout.GameFolder(game));
        var used = _store.DependenciesOf(game).Select(d => d.Backup).Append(_store.Find(game)?.Backup).OfType<string>().ToHashSet(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var backup in Directory.EnumerateDirectories(folder).Where(b => !used.Contains(Path.GetFileName(b))).ToList())
            {
                TryDeleteTree(backup);
            }

            InstallPaths.DeleteIfEmpty(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't clean the backups of {Game}", game);
        }
    }

    /// <summary>Puts every file back as the journal says it was, then drops the transaction's backup and the journal.</summary>
    private void RollBack(InstallJournalFile journal)
    {
        var game = GameId.Parse(journal.Game!);
        var root = journal.InstallPath!;
        var backup = BackupFolder(game, journal.Transaction!);
        if (journal.Phase == PhaseApply)
        {
            if (!Directory.Exists(root))
            {
                throw new InstallFileException(null, root, InstallProblem.GameUnavailable, new DirectoryNotFoundException($"{root} can't be reached."));
            }

            foreach (var entry in (journal.Entries ?? []).Reverse())
            {
                if (entry is not { Path: { } path, Write: true })
                {
                    continue;
                }

                var full = InstallPaths.Resolve(root, path);
                RemoveTempFiles(full);
                var current = Guard(path, full, () => InstallPaths.HashOrNull(full, CancellationToken.None));
                if (current == entry.Before)
                {
                    continue;
                }

                if (entry.Before is null)
                {
                    Guard(path, full, () =>
                    {
                        File.Delete(full);
                        return true;
                    });
                    continue;
                }

                var copy = InstallPaths.Resolve(Path.Combine(backup, entry.BeforeCopy == CopyBefore ? BeforeFolder : FilesFolder), path);
                if (!File.Exists(copy))
                {
                    throw new InstallFileException(path, copy, InstallProblem.BackupDamaged, new FileNotFoundException($"{copy} is missing.", copy));
                }

                if (Guard(path, full, () => InstallPaths.CopyAtomically(copy, full, CancellationToken.None)) != entry.Before)
                {
                    throw new InstallFileException(path, copy, InstallProblem.BackupDamaged, new IOException($"{copy} doesn't have the expected content."));
                }
            }

            foreach (var folder in (journal.CreatedFolders ?? []).Reverse())
            {
                InstallPaths.DeleteIfEmpty(InstallPaths.Resolve(root, folder));
            }
        }

        InstallPaths.DeleteTree(backup);
        InstallPaths.DeleteIfEmpty(Path.GetDirectoryName(backup)!);
        DeleteJournal();
    }

    /// <summary>Rollback during a session; on failure the journal stays and the next start retries.</summary>
    private bool TryRollBack(InstallJournalFile journal)
    {
        try
        {
            RollBack(journal);
            return true;
        }
        catch (Exception e) when (IsTransactionError(e))
        {
            _logger.LogError(e, "Rolling back the install into {Game} failed; it is retried on the next start", journal.Game);
            return false;
        }
    }

    /// <summary>A temp file <see cref="InstallPaths.CopyAtomically"/> left next to <paramref name="full"/> when the process died.</summary>
    private static void RemoveTempFiles(string full)
    {
        var folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var temp in Directory.EnumerateFiles(folder, $".{Path.GetFileName(full)}.*{TempSuffix}"))
        {
            InstallPaths.TryDelete(temp);
        }
    }

    private (List<string> Restored, List<string> Removed, List<string> Kept) RestoreOriginals(
        GameId game, IReadOnlyList<IInstalledFiles> parts, string root, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        List<string> restored = [], removed = [], kept = [];
        var total = parts.Sum(p => p.Files.Count);
        var done = 0;
        foreach (var part in parts)
        {
            var backup = Path.Combine(BackupFolder(game, part.Backup), FilesFolder);
            foreach (var file in part.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, InstallStage.Restoring, done++, total);
                var full = InstallPaths.Resolve(root, file.Path);
                RemoveTempFiles(full);
                var current = Guard(file.Path, full, () =>
                {
                    BeforeFile?.Invoke(InstallStage.Restoring, file.Path);
                    return InstallPaths.HashOrNull(full, cancellationToken);
                });
                if (file.OriginalSha256 is { } original)
                {
                    if (current == original)
                    {
                        continue;
                    }

                    if (current is not null && current != file.InstalledSha256)
                    {
                        kept.Add(file.Path);
                        continue;
                    }

                    var copy = InstallPaths.Resolve(backup, file.Path);
                    if (!File.Exists(copy))
                    {
                        throw new InstallFileException(file.Path, copy, InstallProblem.BackupDamaged, new FileNotFoundException($"{copy} is missing.", copy));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (Guard(file.Path, full, () => InstallPaths.CopyAtomically(copy, full, cancellationToken)) != original)
                    {
                        throw new InstallFileException(file.Path, copy, InstallProblem.BackupDamaged, new IOException($"{copy} doesn't have the expected content."));
                    }

                    restored.Add(file.Path);
                }
                else if (current == file.InstalledSha256)
                {
                    Guard(file.Path, full, () =>
                    {
                        File.Delete(full);
                        return true;
                    });
                    removed.Add(file.Path);
                }
                else if (current is not null)
                {
                    kept.Add(file.Path);
                }
            }
        }

        var folders = new List<string>();
        foreach (var generated in parts.SelectMany(p => p.GeneratedFiles))
        {
            if (generated.EndsWith('/'))
            {
                folders.Add(generated[..^1]);
                continue;
            }

            var matches = generated.IndexOfAny(['*', '?']) < 0
                ? [InstallPaths.Resolve(root, generated)]
                : GeneratedMatches(root, generated);
            foreach (var full in matches.Where(File.Exists))
            {
                Guard(generated, full, () =>
                {
                    File.Delete(full);
                    return true;
                });
                removed.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
            }
        }

        foreach (var folder in parts.SelectMany(p => p.AddedFolders).Concat(folders).Distinct(PathNames.Comparer).OrderByDescending(f => f.Count(c => c == '/')))
        {
            InstallPaths.DeleteIfEmpty(InstallPaths.Resolve(root, folder));
        }

        Report(progress, InstallStage.Restoring, total, total);
        return (restored, removed, kept);
    }

    private RecoveryResult RecoverLocked(CancellationToken cancellationToken)
    {
        InstallJournalFile? journal;
        try
        {
            journal = ReadJournal();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(e, "Can't read the install journal {File}", _paths.InstallJournal);
            return new RecoveryResult(RecoveryOutcome.Failed, Failure: new InstallFailure(InstallProblem.IoError, Detail: e.Message, FilesRestored: false));
        }

        if (journal is null)
        {
            return new RecoveryResult(RecoveryOutcome.Nothing);
        }

        if (journal is not { Game: { } gameText, InstallPath.Length: > 0, Transaction: { } transaction }
            || !GameId.TryParse(gameText, out var game)
            || !InstallPaths.IsSafeName(transaction)
            || (journal.Replaces is { } replaces && !InstallPaths.IsSafeName(replaces)))
        {
            _logger.LogError("The install journal {File} is damaged; it is left for a look by hand", _paths.InstallJournal);
            return new RecoveryResult(RecoveryOutcome.Failed, Failure: new InstallFailure(InstallProblem.IoError, Detail: "The install journal is damaged.", FilesRestored: false));
        }

        var packId = journal.Pack?.Pack ?? _store.Find(game)?.PackId;
        try
        {
            if (journal.Operation == OperationUninstall)
            {
                var removesPack = journal.RemovesPack ?? true;
                var record = _store.Find(game);
                var keep = (journal.KeepDependencies ?? []).Select(ParseKey).OfType<DependencyKey>().ToList();
                if (removesPack && record?.Backup != transaction)
                {
                    // The records were saved before the crash: only the clean-up was left.
                    DeleteUnusedBackups(game);
                    DeleteJournal();
                }
                else
                {
                    var (removed, kept) = SplitDependencies(game, record, removesPack, keep);
                    IReadOnlyList<IInstalledFiles> parts = removesPack ? [record!, .. removed] : [.. removed];
                    RestoreOriginals(game, parts, journal.InstallPath!, null, cancellationToken);
                    FinishUninstall(game, removesPack ? null : record, kept);
                }

                _logger.LogWarning("Finished the interrupted uninstall of {Pack} from {Game}", packId, game);
                return new RecoveryResult(RecoveryOutcome.UninstallFinished, game, packId);
            }

            if (_store.Find(game)?.Backup == transaction)
            {
                // The record was saved before the crash: everything was written and checked.
                FinishInstall(journal);
                _logger.LogWarning("Completed the interrupted install of {Pack} into {Game}", packId, game);
                return new RecoveryResult(RecoveryOutcome.Completed, game, packId);
            }

            RollBack(journal);
            _logger.LogWarning("Rolled back the interrupted install of {Pack} into {Game}", packId, game);
            return new RecoveryResult(RecoveryOutcome.RolledBack, game, packId);
        }
        catch (Exception e) when (IsTransactionError(e) && e is not OperationCanceledException)
        {
            var failure = Describe(e, filesRestored: false);
            _logger.LogError(e, "Finishing the interrupted transaction for {Game} failed at {File}: {Problem}", game, failure.File, failure.Problem);
            return new RecoveryResult(RecoveryOutcome.Failed, game, packId, failure);
        }
    }

    private FileStream? TryLock()
    {
        Directory.CreateDirectory(_paths.Data);
        try
        {
            return new FileStream(_paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private InstallJournalFile? ReadJournal()
    {
        if (!File.Exists(_paths.InstallJournal))
        {
            return null;
        }

        return JsonSerializer.Deserialize(File.ReadAllBytes(_paths.InstallJournal), InstallJsonContext.Default.InstallJournalFile);
    }

    private void WriteJournal(InstallJournalFile journal) =>
        AtomicFile.WriteAllBytes(_paths.InstallJournal, JsonSerializer.SerializeToUtf8Bytes(journal, InstallJsonContext.Default.InstallJournalFile));

    private void DeleteJournal() => File.Delete(_paths.InstallJournal);

    private void TryDeleteJournal()
    {
        try
        {
            DeleteJournal();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't delete the install journal {File}", _paths.InstallJournal);
        }
    }

    private void TryDeleteTree(string folder)
    {
        try
        {
            InstallPaths.DeleteTree(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Can't delete {Folder}", folder);
        }
    }

    private string BackupFolder(GameId game, string transaction) => Path.Combine(_paths.Backups, RegistryLayout.GameFolder(game), transaction);

    private string NewTransactionName() =>
        _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-"
        + Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal)[..8];

    private static void Report(IProgress<InstallProgress>? progress, InstallStage stage, int done, int total) =>
        progress?.Report(new InstallProgress(stage, done, total));

    private static bool IsTransactionError(Exception e) =>
        e is InstallFileException or IOException or UnauthorizedAccessException or OperationCanceledException;

    /// <summary>Runs a file operation; an OS error comes out as <see cref="InstallFileException"/> naming the file.</summary>
    private static T Guard<T>(string? path, string? full, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not InstallFileException)
        {
            throw new InstallFileException(path, full, InstallErrors.Classify(e, full), e);
        }
    }

    private static InstallFailure Describe(Exception e, bool filesRestored) => e switch
    {
        InstallFileException file => new InstallFailure(file.Problem, file.File, file.InnerException?.Message ?? file.Message, filesRestored),
        OperationCanceledException => new InstallFailure(InstallProblem.IoError, Detail: "Cancelled.", FilesRestored: filesRestored),
        _ => new InstallFailure(InstallErrors.Classify(e, null), Detail: e.Message, FilesRestored: filesRestored),
    };

    /// <param name="Write">Changes the game file (its content differs from the new one).</param>
    /// <param name="Before">Hash of the game file before; <c>null</c> = absent.</param>
    /// <param name="BeforeCopy">Where a rollback finds it (<see cref="CopyBackup"/>, <see cref="CopyBefore"/>).</param>
    /// <param name="OriginalFrom">Where the original is copied into the backup from: the game file or the old backup.</param>
    private sealed record PlanEntry(
        int Part,
        string Path,
        string Full,
        string? Source,
        bool Write,
        string? Before,
        string? BeforeCopy,
        string? Original,
        string? OriginalFrom,
        string Installed,
        long Size);

    private sealed record InstallPlan(IReadOnlyList<PlanEntry> Entries, InstalledPack Record, IReadOnlyList<InstalledDependency> Dependencies, InstallJournalFile Journal, string BackupFolder);

    /// <summary>
    /// What one transaction installs: a dependency (<see cref="DependencyId"/>) or the pack (<c>null</c>), with the record
    /// it installs over (re-apply), whose originals stay.
    /// </summary>
    private sealed record Part(string? DependencyId, string? Version, IReadOnlyList<InstallFile> Files, IReadOnlyList<string> Generated, IInstalledFiles? Old, InstalledDependency? OldDependency);
}

/// <summary>A file operation of a transaction failed: the file and the problem.</summary>
internal sealed class InstallFileException(string? file, string? fullPath, InstallProblem problem, Exception inner)
    : IOException($"{problem} on {fullPath ?? file}: {inner.Message}", inner)
{
    public string? File { get; } = file;

    public string? FullPath { get; } = fullPath;

    public InstallProblem Problem { get; } = problem;
}
