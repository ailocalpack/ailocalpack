using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Toolkit.Dependencies;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Install;

/// <summary>
/// Installs, re-applies and uninstalls packs (design §4a; task 29), one operation at a time: download into the cache
/// (<see cref="PackFetcher"/>), the whitelisted dependencies it needs from their official releases (task 32:
/// <see cref="DependencyPreparer"/>, hash checked, unpacked on a copy; installed in the same transaction and shared
/// with other packs), uninstall the game's other pack (keeping the dependencies the new one shares), compatibility check against the player's files
/// (<see cref="PackCompatibilityChecker"/>), rebuild of the touched files from the strings into a staging folder (the
/// recipe tool, reading the player's own files; then the font swap when the game's font lacks the translation's letters,
/// <see cref="FontFixer"/>, downloading a whitelisted font that isn't bundled), then the transaction (<see cref="PackInstaller"/>: backup, write,
/// verify, rollback on failure). Only the app's own code runs; nothing from the pack executes. <see cref="Current"/>
/// and <see cref="Changed"/> give the step for the progress block, the list row and the library card. Never throws:
/// every failure is an <see cref="InstallOutcome.Error"/> with a log in the UI language.
/// </summary>
public sealed class PackInstallService
{
    private readonly PackIndex _packIndex;
    private readonly PackFetcher _fetcher;
    private readonly PackCache _cache;
    private readonly PackCompatibilityChecker _checker;
    private readonly RecipeTools _recipes;
    private readonly FontFixer _fonts;
    private readonly DependencyPreparer _dependencies;
    private readonly PackInstaller _installer;
    private readonly InstalledPackStore _store;
    private readonly AntiCheatPolicy _policy;
    private readonly FileHashCache _hashes;
    private readonly AppPaths _paths;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private InstallOperation? _current;

    public PackInstallService(
        PackIndex packIndex,
        PackFetcher fetcher,
        PackCache cache,
        PackCompatibilityChecker checker,
        RecipeTools recipes,
        FontFixer fonts,
        DependencyPreparer dependencies,
        PackInstaller installer,
        InstalledPackStore store,
        AntiCheatPolicy policy,
        FileHashCache hashes,
        AppPaths paths,
        Localizer localizer,
        TimeProvider time,
        ILogger<PackInstallService>? logger = null)
    {
        _packIndex = packIndex;
        _fetcher = fetcher;
        _cache = cache;
        _checker = checker;
        _recipes = recipes;
        _fonts = fonts;
        _dependencies = dependencies;
        _installer = installer;
        _store = store;
        _policy = policy;
        _hashes = hashes;
        _paths = paths;
        _localizer = localizer;
        _time = time;
        _logger = logger ?? NullLogger<PackInstallService>.Instance;
    }

    /// <summary>Raised (on a pool thread) when an operation starts, moves on or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>The operation running now, or <c>null</c>.</summary>
    public InstallOperation? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Staging folders of rebuilt files (under the cache, so a crash leaves nothing in the data folder).</summary>
    private string StagingRoot => Path.Combine(_paths.Cache, "staging");

    /// <summary>Installs <paramref name="pack"/> into the game, replacing the game's other pack.</summary>
    public Task<InstallOutcome> InstallAsync(Game game, RegistryPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        var replaces = _store.Find(game.Id) is not null;
        InstallStep[] steps = replaces
            ? [InstallStep.Download, InstallStep.RemovePrevious, InstallStep.Check, InstallStep.Rebuild, InstallStep.BackUp, InstallStep.Write, InstallStep.Verify]
            : [InstallStep.Download, InstallStep.Check, InstallStep.Rebuild, InstallStep.BackUp, InstallStep.Write, InstallStep.Verify];
        var operation = new InstallOperation(game.Id, InstallOperationKind.Install, pack.Id, pack.Title, steps);
        return RunAsync(operation, game, run => RunInstall(run, game, pack, cancellationToken));
    }

    /// <summary>Applies the installed pack again, its installed revision (task 33 calls it after a game update).</summary>
    public Task<InstallOutcome> ReapplyAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var record = _store.Find(game.Id);
        var operation = new InstallOperation(
            game.Id,
            InstallOperationKind.Reapply,
            record?.PackId ?? string.Empty,
            TitleOf(game.Id, record),
            [InstallStep.Download, InstallStep.Rebuild, InstallStep.BackUp, InstallStep.Write, InstallStep.Verify]);
        return RunAsync(operation, game, run => RunReapply(run, game, record, cancellationToken));
    }

    /// <summary>Restores the original files of the game's pack.</summary>
    public Task<InstallOutcome> UninstallAsync(Game game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var record = _store.Find(game.Id);
        var operation = new InstallOperation(game.Id, InstallOperationKind.Uninstall, record?.PackId ?? string.Empty, TitleOf(game.Id, record), [InstallStep.Restore]);
        return RunAsync(operation, game, run => RunUninstall(run, game, cancellationToken));
    }

    /// <summary>The title of an installed pack from the pack list, else its ID.</summary>
    public string TitleOf(GameId game, InstalledPack? record) => record is null ? string.Empty
        : _packIndex.Current?.Index?.PacksFor(game, record.Language).FirstOrDefault(p => p.Id == record.PackId)?.Title ?? record.PackId;

    private async Task<InstallOutcome> RunAsync(InstallOperation operation, Game game, Func<Run, Task<InstallOutcome>> body)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                return new InstallOutcome(operation.Kind, game, operation.PackId, operation.Title, new InstallError(InstallErrorKind.Busy), []);
            }

            _current = operation;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        var run = new Run(this, operation, game);
        try
        {
            return await Task.Run(() => body(run)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{Kind} of {Pack} for {Game} failed unexpectedly", operation.Kind, operation.PackId, game.Id);
            return run.Fail(new InstallError(InstallErrorKind.Unexpected, Detail: e.Message));
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<InstallOutcome> RunInstall(Run run, Game game, RegistryPack pack, CancellationToken cancellationToken)
    {
        var index = _packIndex.Current?.Index;
        if (Blocked(run, game, index) is { } blocked)
        {
            return blocked;
        }

        run.Step(InstallStep.Download);
        if (_packIndex.Current?.Location is not { } location)
        {
            return run.Fail(new InstallError(InstallErrorKind.NotInRegistry));
        }

        var fetch = await _fetcher.FetchAsync(location, pack, run.DownloadProgress(), cancellationToken).ConfigureAwait(false);
        if (fetch.Pack is not { } cached)
        {
            return run.Fail(new InstallError(InstallErrorKind.Fetch, Detail: fetch.Detail, Fetch: fetch.Problem));
        }

        run.LogFetch(fetch, pack.Revision);
        if (Unsupported(run, cached.Manifest, game) is { } unsupported)
        {
            return unsupported;
        }

        var staging = Path.Combine(StagingRoot, Path.GetRandomFileName());
        var removedPrevious = false;
        try
        {
            // Dependencies before the previous pack goes: a download that fails leaves the game as it was.
            var prepared = await PrepareDependenciesAsync(run, game, cached.Manifest, index, shareInstalled: true, staging, cancellationToken).ConfigureAwait(false);
            if (prepared.Failure is { } failed)
            {
                return failed;
            }

            if (_store.Find(game.Id) is { } previous)
            {
                run.Step(InstallStep.RemovePrevious);
                var keep = cached.Manifest.Dependencies.Select(d => new DependencyKey(d.Id, d.Version)).ToList();
                var removed = _installer.Uninstall(game, run.TransactionProgress(), cancellationToken, keep);
                if (removed.Failure is { } failure)
                {
                    return run.Fail(new InstallError(InstallErrorKind.Transaction, failure.File, failure.Detail, Transaction: failure));
                }

                removedPrevious = true;
                run.Log(nameof(Strings.Install_Log_Removed), TitleOf(game.Id, previous), previous.Revision);
                run.LogRemovedDependencies(removed);
            }

            run.Step(InstallStep.Check);
            var compatibility = _checker.Check(cached.Manifest, game, cancellationToken);
            _hashes.Save();
            run.Log(nameof(Strings.Install_Log_Checked), PackCompatibilityTexts.Short(compatibility.Verdict, _localizer));
            if (!compatibility.CanInstall)
            {
                return run.Fail(new InstallError(InstallErrorKind.Incompatible, Compatibility: compatibility));
            }

            var outcome = await ApplyAsync(run, game, cached, reapply: false, index, prepared.Dependencies, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded && removedPrevious)
            {
                // The previous pack is gone and the new one didn't come: the dependencies kept for it go too.
                CleanUpDependencies(game);
            }

            return outcome;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Dependencies no pack uses any more (after a failed replacement) are removed; a failure is only logged.</summary>
    private void CleanUpDependencies(Game game)
    {
        var result = _installer.RemoveUnusedDependencies(game);
        if (result.Failure is { } failure)
        {
            _logger.LogWarning("Couldn't remove the unused dependencies of {Game}: {Problem} {File} {Detail}", game.Id, failure.Problem, failure.File, failure.Detail);
        }
    }

    /// <summary>
    /// The pack's dependencies looked up in the whitelist (an unknown one refuses the pack), their release files for the
    /// game's platform downloaded (step <see cref="InstallStep.Dependency"/>, added when the pack has one) and unpacked
    /// under <paramref name="staging"/>. With <paramref name="shareInstalled"/>, one installed in the same version is shared, not downloaded.
    /// </summary>
    private async Task<(IReadOnlyList<InstallDependency> Dependencies, InstallOutcome? Failure)> PrepareDependenciesAsync(
        Run run, Game game, PackManifest manifest, RegistryIndex? index, bool shareInstalled, string staging, CancellationToken cancellationToken)
    {
        if (manifest.Dependencies.Count == 0)
        {
            return ([], null);
        }

        run.AddStep(InstallStep.Dependency, after: InstallStep.Download);
        run.Step(InstallStep.Dependency);
        var whitelist = index?.Dependencies ?? DependencyWhitelist.Empty;
        var resolution = _dependencies.Resolve(game, manifest.Dependencies, whitelist, shareInstalled ? _store.DependenciesOf(game.Id) : null);
        string? Name(PackDependency? dependency) => dependency is null ? null : whitelist.Find(dependency.Id)?.Name;
        switch (resolution.Problem)
        {
            case DependencyProblem.Unknown:
                return ([], run.Fail(new InstallError(InstallErrorKind.DependencyUnknown, Dependencies: [resolution.Failed!])));
            case DependencyProblem.PlatformUnknown:
            case DependencyProblem.NoAsset:
                return ([], run.Fail(new InstallError(
                    InstallErrorKind.DependencyPlatform, Dependencies: [resolution.Failed!], DependencyName: Name(resolution.Failed), Platform: resolution.Platform?.ToString())));
        }

        foreach (var shared in resolution.Shared)
        {
            run.Log(nameof(Strings.Install_Log_DependencyShared), shared.Dependency.Name, shared.Version.Version);
        }

        var prepared = await _dependencies.PrepareAsync(resolution.Choices, Path.Combine(staging, "dependencies"), run.StepProgress(InstallStep.Dependency), cancellationToken).ConfigureAwait(false);
        foreach (var done in prepared.Prepared)
        {
            run.LogDependencyFetch(done);
        }

        if (!prepared.Succeeded)
        {
            var choice = prepared.Failed!;
            var dependency = new PackDependency(choice.Dependency.Id, choice.Version.Version);
            _logger.LogWarning("Dependency {Id} {Version} for {Game}: {Problem} {Detail}", dependency.Id, dependency.Version, game.Id, prepared.Problem, prepared.Detail);
            return ([], run.Fail(new InstallError(
                prepared.Problem == DependencyProblem.Archive ? InstallErrorKind.DependencyArchive : InstallErrorKind.DependencyFetch,
                Detail: prepared.Detail,
                Fetch: prepared.Fetch?.Problem ?? PackFetchProblem.None,
                Dependencies: [dependency],
                DependencyName: choice.Dependency.Name)));
        }

        // Shared ones are named without files: the transaction adds the pack to their users.
        return ([.. resolution.Shared.Select(c => new InstallDependency(c.Dependency.Id, c.Version.Version, [])), .. prepared.Prepared.Select(p => p.Install)], null);
    }

    private async Task<InstallOutcome> RunReapply(Run run, Game game, InstalledPack? record, CancellationToken cancellationToken)
    {
        var index = _packIndex.Current?.Index;
        if (record is null)
        {
            return run.Fail(new InstallError(InstallErrorKind.Transaction, Transaction: new InstallFailure(InstallProblem.NotInstalled)));
        }

        if (Blocked(run, game, index) is { } blocked)
        {
            return blocked;
        }

        run.Step(InstallStep.Download);

        // The installed revision: the cache keeps it since the install; else the registry, while it still lists it.
        var cached = _cache.Find(game.Id, record.Language, record.PackId, record.Revision);
        if (cached is null)
        {
            var pack = index?.PacksFor(game.Id, record.Language).FirstOrDefault(p => p.Id == record.PackId && p.Revision == record.Revision);
            if (pack is null || _packIndex.Current?.Location is not { } location)
            {
                return run.Fail(new InstallError(InstallErrorKind.NotInRegistry));
            }

            var fetch = await _fetcher.FetchAsync(location, pack, run.DownloadProgress(), cancellationToken).ConfigureAwait(false);
            if (fetch.Pack is null)
            {
                return run.Fail(new InstallError(InstallErrorKind.Fetch, Detail: fetch.Detail, Fetch: fetch.Problem));
            }

            run.LogFetch(fetch, pack.Revision);
            cached = fetch.Pack;
        }
        else
        {
            run.Log(nameof(Strings.Install_Log_FromCache), record.Revision);
        }

        if (Unsupported(run, cached.Manifest, game) is { } unsupported)
        {
            return unsupported;
        }

        var staging = Path.Combine(StagingRoot, Path.GetRandomFileName());
        try
        {
            var prepared = await PrepareDependenciesAsync(run, game, cached.Manifest, index, shareInstalled: false, staging, cancellationToken).ConfigureAwait(false);
            return prepared.Failure ?? await ApplyAsync(run, game, cached, reapply: true, index, prepared.Dependencies, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private Task<InstallOutcome> RunUninstall(Run run, Game game, CancellationToken cancellationToken)
    {
        run.Step(InstallStep.Restore);
        var result = _installer.Uninstall(game, run.TransactionProgress(), cancellationToken);
        if (result.Failure is { } failure)
        {
            return Task.FromResult(run.Fail(new InstallError(InstallErrorKind.Transaction, failure.File, failure.Detail, Transaction: failure)));
        }

        run.Log(nameof(Strings.Install_Log_Uninstalled), result.Restored.Count, result.Removed.Count);
        foreach (var kept in result.Kept)
        {
            run.Log(nameof(Strings.Install_Log_Kept), kept);
        }

        run.LogRemovedDependencies(result);

        return Task.FromResult(run.Done(uninstall: result));
    }

    /// <summary>Rebuilds the touched files on a copy (fonts swapped when needed), then installs (or re-applies) them as one transaction.</summary>
    private async Task<InstallOutcome> ApplyAsync(
        Run run, Game game, CachedPack cached, bool reapply, RegistryIndex? index, IReadOnlyList<InstallDependency> dependencies, CancellationToken cancellationToken)
    {
        var manifest = cached.Manifest;
        run.Step(InstallStep.Rebuild);
        var strings = cached.ReadStrings();
        if (strings.Value is not { } table)
        {
            return run.Fail(new InstallError(InstallErrorKind.InvalidStrings, Detail: string.Join("; ", strings.Errors.Select(e => $"{e.Field}: {e.Message}"))));
        }

        var tool = _recipes.Find(manifest.Recipe!.Tool)!;
        var staging = Path.Combine(StagingRoot, Path.GetRandomFileName());
        try
        {
            RebuildResult rebuilt;
            FontFixResult fonts;
            var output = new DirectoryRebuildOutput(staging);
            try
            {
                rebuilt = tool.Rebuild(game.InstallPath, table.Language, table.Strings, output, cancellationToken);
                run.Log(nameof(Strings.Install_Log_Rebuilt), rebuilt.ChangedFiles.Count, rebuilt.Applied);
                if (rebuilt.UnknownKeys.Count > 0)
                {
                    run.Log(nameof(Strings.Install_Log_UnknownKeys), rebuilt.UnknownKeys.Count);
                }

                fonts = await _fonts.ApplyAsync(tool, game.InstallPath, table.Language, table.Strings, manifest.Fonts, output, run.FontProgress(), cancellationToken).ConfigureAwait(false);
            }
            catch (RecipeException e)
            {
                _logger.LogWarning(e, "Rebuilding {Pack} for {Game} failed at {File}", manifest.Id, game.Id, e.File);
                return run.Fail(new InstallError(InstallErrorKind.Recipe, e.File, e.Message));
            }

            run.LogFonts(fonts);
            if (fonts.Failure is { } fontFailure)
            {
                return run.Fail(new InstallError(InstallErrorKind.Font, Detail: fontFailure.Detail, Font: fontFailure.Problem, FontName: fonts.FailedFont?.Name));
            }

            var files = rebuilt.ChangedFiles.Union(fonts.Files.Files, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(path => new InstallFile(path, Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();
            var generated = rebuilt.GeneratedFiles.Union(fonts.Files.GeneratedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var request = new InstallRequest(game, manifest.Language, manifest.Id, manifest.Revision, files) { GeneratedFiles = generated, Dependencies = dependencies };
            run.Step(InstallStep.BackUp);
            var result = reapply
                ? _installer.Reapply(request, index, run.TransactionProgress(), cancellationToken)
                : _installer.Install(request, index, run.TransactionProgress(), cancellationToken);
            if (result.Failure is { } failure)
            {
                return run.Fail(new InstallError(InstallErrorKind.Transaction, failure.File, failure.Detail, Transaction: failure));
            }

            var added = result.Installed!.Files.Count(f => f.OriginalSha256 is null);
            run.Log(nameof(Strings.Install_Log_Installed), result.Installed.Files.Count - added, added);
            var whitelist = index?.Dependencies ?? DependencyWhitelist.Empty;
            foreach (var dependency in result.Dependencies.Where(d => !result.SharedDependencies.Contains(d.Id)))
            {
                run.Log(nameof(Strings.Install_Log_DependencyInstalled), whitelist.Find(dependency.Id)?.Name ?? dependency.Id, dependency.Version, dependency.Files.Count);
            }

            return run.Done(installed: result.Installed);
        }
        catch (GameBlockedException e)
        {
            return run.Fail(new InstallError(InstallErrorKind.Blocked, Detail: e.Message));
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private InstallOutcome? Blocked(Run run, Game game, RegistryIndex? index)
    {
        try
        {
            GameBlocks.EnsureAllowed(game, _policy, index, BlockedOperation.Install);
            return null;
        }
        catch (GameBlockedException e)
        {
            return run.Fail(new InstallError(InstallErrorKind.Blocked, Detail: e.Message));
        }
    }

    /// <summary>Patch packs (task 34) aren't installed yet; a recipe the toolkit lacks never.</summary>
    private InstallOutcome? Unsupported(Run run, PackManifest manifest, Game game)
    {
        if (manifest.Patch is not null || manifest.Recipe is null)
        {
            return run.Fail(new InstallError(InstallErrorKind.PatchUnsupported));
        }

        var support = _recipes.Check(manifest.Recipe, game.Engine);
        if (support != RecipeToolSupport.Supported || _recipes.Find(manifest.Recipe.Tool) is null)
        {
            return run.Fail(new InstallError(
                InstallErrorKind.Incompatible,
                Compatibility: new PackCompatibility(
                    PackCompatibilityVerdict.Incompatible,
                    support switch
                    {
                        RecipeToolSupport.TooNew => PackCompatibilityReason.RecipeToolTooNew,
                        RecipeToolSupport.EngineUnsupported => PackCompatibilityReason.EngineUnsupported,
                        _ => PackCompatibilityReason.RecipeToolUnknown,
                    },
                    [],
                    null,
                    null,
                    0,
                    [],
                    [],
                    RecipeTool: manifest.Recipe,
                    Engine: game.Engine)));
        }

        return null;
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't delete the staging folder {Folder}", folder);
        }
    }

    private void Report(InstallOperation operation)
    {
        lock (_lock)
        {
            if (_current is null || _current.Game != operation.Game)
            {
                return;
            }

            _current = operation;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One operation in flight: its progress and log.</summary>
    private sealed class Run(PackInstallService service, InstallOperation operation, Game game)
    {
        private readonly List<string> _log = [];
        private InstallOperation _operation = operation;

        /// <summary>Adds a step the operation turned out to need (a dependency download, once the manifest is known).</summary>
        public void AddStep(InstallStep step, InstallStep after)
        {
            if (_operation.Steps.Contains(step))
            {
                return;
            }

            var steps = _operation.Steps.ToList();
            steps.Insert(steps.IndexOf(after) + 1, step);
            _operation = _operation with { Steps = steps };
            service.Report(_operation);
        }

        public void Step(InstallStep step)
        {
            var index = Math.Max(0, _operation.Steps.ToList().IndexOf(step));
            _operation = _operation with { StepIndex = index, Fraction = 0 };
            service.Report(_operation);
        }

        public void Log(string key, params object[] args)
        {
            var time = TimeZoneInfo.ConvertTime(service._time.GetUtcNow(), service._time.LocalTimeZone).ToString("T", service._localizer.Culture);
            _log.Add($"{time}  {(args.Length == 0 ? service._localizer[key] : service._localizer.Format(key, args))}");
        }

        public void LogFetch(PackFetchResult fetch, int revision)
        {
            if (fetch.FromCache)
            {
                Log(nameof(Strings.Install_Log_FromCache), revision);
            }
            else
            {
                Log(nameof(Strings.Install_Log_Downloaded), revision, fetch.Downloaded.ToString("N0", service._localizer.Culture));
            }
        }

        public IProgress<PackFetchProgress> DownloadProgress() => new Callback<PackFetchProgress>(p =>
        {
            if (p.Total > 0 && _operation.Step == InstallStep.Download)
            {
                _operation = _operation with { Fraction = (double)p.Done / p.Total };
                service.Report(_operation);
            }
        });

        /// <summary>A whitelisted font downloaded during the rebuild step.</summary>
        public IProgress<PackFetchProgress> FontProgress() => StepProgress(InstallStep.Rebuild);

        /// <summary>A download moving the bar of <paramref name="step"/>.</summary>
        public IProgress<PackFetchProgress> StepProgress(InstallStep step) => new Callback<PackFetchProgress>(p =>
        {
            if (p.Total > 0 && _operation.Step == step)
            {
                _operation = _operation with { Fraction = (double)p.Done / p.Total };
                service.Report(_operation);
            }
        });

        /// <summary>"BepInEx 5.4.23.2: downloaded 637,157 bytes from its official release, hash checked." / taken from the cache.</summary>
        public void LogDependencyFetch(PreparedDependency prepared)
        {
            var choice = prepared.Choice;
            if (prepared.Fetch.FromCache)
            {
                Log(nameof(Strings.Install_Log_DependencyCached), choice.Dependency.Name, choice.Version.Version, choice.Asset.Platform.ToString());
            }
            else
            {
                Log(nameof(Strings.Install_Log_DependencyDownloaded), choice.Dependency.Name, choice.Version.Version, choice.Asset.Platform.ToString(),
                    choice.Asset.Size.ToString("N0", service._localizer.Culture), choice.Asset.Url.Host);
            }
        }

        /// <summary>The dependencies an uninstall removed, and those it kept for the next pack.</summary>
        public void LogRemovedDependencies(UninstallResult result)
        {
            var whitelist = service._packIndex.Current?.Index?.Dependencies ?? DependencyWhitelist.Empty;
            foreach (var dependency in result.RemovedDependencies)
            {
                Log(nameof(Strings.Install_Log_DependencyRemoved), whitelist.Find(dependency.Id)?.Name ?? dependency.Id, dependency.Version);
            }
        }

        /// <summary>What the font check found and did, a line each.</summary>
        public void LogFonts(FontFixResult fonts)
        {
            foreach (var id in fonts.UnknownFonts)
            {
                Log(nameof(Strings.Install_Log_FontUnknown), id);
            }

            if (fonts.Check is not { } check)
            {
                return;
            }

            foreach (var font in check.Fonts.Where(f => f.Problem is not null))
            {
                Log(nameof(Strings.Install_Log_FontUnreadable), font.Font.Name);
            }

            if (!check.NeedsSwap)
            {
                Log(nameof(Strings.Install_Log_FontsOk));
                return;
            }

            foreach (var font in check.ToSwap)
            {
                if (fonts.Replacement is { } replacement)
                {
                    Log(nameof(Strings.Install_Log_FontSwapped), font.DisplayName, font.MissingLetters.Count, FontFixer.Sample(font.MissingLetters), replacement.Name);
                }
                else
                {
                    Log(nameof(Strings.Install_Log_FontMissing), font.DisplayName, font.MissingLetters.Count, FontFixer.Sample(font.MissingLetters));
                }
            }

            if (fonts.Replacement is { } chosen && fonts.Uncovered.Count > 0)
            {
                Log(nameof(Strings.Install_Log_FontUncovered), chosen.Name, fonts.Uncovered.Count, FontFixer.Sample(fonts.Uncovered));
            }
        }

        /// <summary>The transaction's own stages, mapped onto the steps (a rollback keeps the step it failed in).</summary>
        public IProgress<InstallProgress> TransactionProgress() => new Callback<InstallProgress>(p =>
        {
            var step = p.Stage switch
            {
                InstallStage.Checking or InstallStage.BackingUp => InstallStep.BackUp,
                InstallStage.Writing => InstallStep.Write,
                InstallStage.Verifying => InstallStep.Verify,
                _ => _operation.Steps.Contains(InstallStep.Restore) ? InstallStep.Restore : _operation.Step,
            };
            if (_operation.Step == InstallStep.RemovePrevious || !_operation.Steps.Contains(step))
            {
                step = _operation.Step;
            }

            var index = _operation.Steps.ToList().IndexOf(step);
            _operation = _operation with { StepIndex = index, Fraction = p.Total > 0 ? (double)p.Done / p.Total : 0 };
            service.Report(_operation);
        });

        public InstallOutcome Fail(InstallError error)
        {
            Log(nameof(Strings.Install_Log_Failed), InstallTexts.Reason(error, service._localizer));
            if (error.Detail is { Length: > 0 } detail)
            {
                _log.Add("    " + detail);
            }

            if (error.Kind == InstallErrorKind.Transaction)
            {
                Log(error.FilesRestored ? nameof(Strings.Install_Log_Restored) : nameof(Strings.Install_Log_NotRestored));
            }

            service._logger.LogWarning(
                "{Kind} of {Pack} for {Game} failed: {Error} {Problem} {File} {Detail}",
                _operation.Kind, _operation.PackId, game.Id, error.Kind, error.Transaction?.Problem.ToString() ?? error.Fetch.ToString(), error.File, error.Detail);
            return new InstallOutcome(_operation.Kind, game, _operation.PackId, _operation.Title, error, [.. _log]);
        }

        public InstallOutcome Done(InstalledPack? installed = null, UninstallResult? uninstall = null)
        {
            service._logger.LogInformation("{Kind} of {Pack} for {Game} done", _operation.Kind, _operation.PackId, game.Id);
            return new InstallOutcome(_operation.Kind, game, _operation.PackId, _operation.Title, null, [.. _log], installed, uninstall);
        }
    }

    /// <summary>A synchronous progress callback (no posting to a context: the service raises its own event).</summary>
    private sealed class Callback<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
