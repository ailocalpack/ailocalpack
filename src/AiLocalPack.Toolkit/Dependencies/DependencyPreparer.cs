using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Toolkit.Dependencies;

/// <summary>Why a pack's dependencies can't be made ready.</summary>
public enum DependencyProblem
{
    None,

    /// <summary>The dependency (ID and version) isn't on the registry's whitelist: the pack is refused.</summary>
    Unknown,

    /// <summary>The game build's platform can't be told (no readable main executable).</summary>
    PlatformUnknown,

    /// <summary>The whitelist has no release file for the game build's platform.</summary>
    NoAsset,

    /// <summary>The release file couldn't be downloaded or failed its hash check (<see cref="DependencyResolution.Fetch"/>).</summary>
    Fetch,

    /// <summary>The release archive can't be unpacked safely.</summary>
    Archive,
}

/// <summary>Which release files a pack's dependencies take for a game, or why they can't be had.</summary>
/// <param name="Choices">The release file per dependency still to install (those installed already in the version are left out).</param>
/// <param name="Shared">Dependencies installed for the game already in the version needed: shared, nothing to download.</param>
/// <param name="Failed">The dependency the problem is about.</param>
/// <param name="Platform">The game build's platform, when it was needed and found.</param>
public sealed record DependencyResolution(
    IReadOnlyList<DependencyChoice> Choices,
    IReadOnlyList<DependencyChoice> Shared,
    DependencyProblem Problem = DependencyProblem.None,
    PackDependency? Failed = null,
    GamePlatform? Platform = null);

/// <summary>A dependency made ready: its release file fetched (or from the cache) and unpacked for the transaction.</summary>
public sealed record PreparedDependency(DependencyChoice Choice, DependencyFetchResult Fetch, InstallDependency Install);

/// <summary>The dependencies made ready, or why one couldn't be.</summary>
public sealed record DependencyPreparation(
    IReadOnlyList<PreparedDependency> Prepared,
    DependencyProblem Problem = DependencyProblem.None,
    DependencyChoice? Failed = null,
    DependencyFetchResult? Fetch = null,
    string? Detail = null)
{
    public bool Succeeded => Problem == DependencyProblem.None;
}

/// <summary>
/// Gets a pack's whitelisted dependencies ready for the install transaction (task 32), shared by the app and the CLI:
/// <see cref="Resolve"/> looks each one up in the registry's whitelist and picks the release file for the game build's
/// platform (from its main executable: a Windows build under Proton takes the Windows file); <see cref="PrepareAsync"/>
/// downloads it from its official release into the cache (<see cref="DependencyStore"/>, hash checked) and unpacks it
/// into a staging folder (<see cref="DependencyArchive"/>). Nothing touches the game folder.
/// </summary>
public sealed class DependencyPreparer(DependencyStore store, EngineDetector engines)
{
    private readonly DependencyStore _store = store;
    private readonly EngineDetector _engines = engines;

    /// <param name="installed">Dependencies installed for the game now: those in the version needed are shared, not downloaded.</param>
    public DependencyResolution Resolve(Game game, IReadOnlyList<PackDependency> dependencies, DependencyWhitelist whitelist, IReadOnlyList<InstalledDependency>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(whitelist);
        foreach (var dependency in dependencies)
        {
            if (!whitelist.Contains(dependency))
            {
                return new DependencyResolution([], [], DependencyProblem.Unknown, dependency);
            }
        }

        if (dependencies.Count == 0)
        {
            return new DependencyResolution([], []);
        }

        if (Platform(game) is not { } platform)
        {
            return new DependencyResolution([], [], DependencyProblem.PlatformUnknown, dependencies[0]);
        }

        List<DependencyChoice> choices = [], shared = [];
        foreach (var dependency in dependencies)
        {
            if (whitelist.Pick(dependency, platform.Os, platform.Arches, out _) is not { } choice)
            {
                return new DependencyResolution([], [], DependencyProblem.NoAsset, dependency, platform);
            }

            var isShared = installed?.Any(d => d.Id == dependency.Id && d.Version == dependency.Version) == true;
            (isShared ? shared : choices).Add(choice);
        }

        return new DependencyResolution(choices, shared, Platform: platform);
    }

    /// <summary>The platform of the game's build, from its main executable (stored, else the engine's, else the first found).</summary>
    public GamePlatform? Platform(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var folder = game.InstallPath;
        var main = game.MainExecutable ?? _engines.Detect(folder).Match?.MainExecutable;
        foreach (var executable in FolderRecognizer.ListExecutables(folder, main))
        {
            if (GamePlatforms.Detect(Path.Combine(folder, executable.Replace('/', Path.DirectorySeparatorChar)), GamePlatforms.MachineArch) is { } platform)
            {
                return platform;
            }
        }

        return null;
    }

    /// <summary>Downloads (or takes from the cache) and unpacks each choice under <paramref name="staging"/>/&lt;id&gt;.</summary>
    public async Task<DependencyPreparation> PrepareAsync(
        IReadOnlyList<DependencyChoice> choices,
        string staging,
        IProgress<PackFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentException.ThrowIfNullOrEmpty(staging);
        var prepared = new List<PreparedDependency>();
        var total = choices.Sum(c => c.Asset.Size);
        long before = 0;
        foreach (var choice in choices)
        {
            var offset = before;
            var fetch = await _store.GetAsync(
                choice,
                progress is null ? null : new Callback(p => progress.Report(new PackFetchProgress(offset + p.Done, total))),
                cancellationToken).ConfigureAwait(false);
            before += choice.Asset.Size;
            if (fetch.Path is not { } archive)
            {
                return new DependencyPreparation(prepared, DependencyProblem.Fetch, choice, fetch, fetch.Detail);
            }

            IReadOnlyList<DependencyFile> files;
            try
            {
                files = DependencyArchive.Extract(archive, choice.Dependency.Layout, Path.Combine(staging, choice.Dependency.Id), cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DependencyPreparation(prepared, DependencyProblem.Archive, choice, fetch, e.Message);
            }

            var install = new InstallDependency(choice.Dependency.Id, choice.Version.Version, [.. files.Select(f => new InstallFile(f.Path, f.Source))])
            {
                GeneratedFiles = choice.Dependency.Generated,
            };
            prepared.Add(new PreparedDependency(choice, fetch, install));
        }

        return new DependencyPreparation(prepared);
    }

    private sealed class Callback(Action<PackFetchProgress> action) : IProgress<PackFetchProgress>
    {
        public void Report(PackFetchProgress value) => action(value);
    }
}
