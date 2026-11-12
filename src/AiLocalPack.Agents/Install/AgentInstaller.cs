using System.Text.Json;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Agents.Install;

/// <summary>An agent the app installed itself.</summary>
/// <param name="AgentId">The adapter's ID.</param>
/// <param name="Version">Its version.</param>
/// <param name="Folder">Its version folder.</param>
/// <param name="Executable">The program, absolute.</param>
/// <param name="InstalledAt">When it was installed.</param>
public sealed record ManagedAgent(string AgentId, string Version, string Folder, string Executable, DateTimeOffset InstalledAt)
{
    /// <summary>The folder holding the program: what the adapters look in last (<see cref="AgentSource.Managed"/>).</summary>
    public string ExecutableFolder => Path.GetDirectoryName(Executable)!;
}

public enum AgentInstallStage
{
    /// <summary>Finding the release and checking what vouches for its hash.</summary>
    Resolving,

    /// <summary>Downloading (bytes done of total).</summary>
    Downloading,

    /// <summary>Putting the program into its folder.</summary>
    Unpacking,
}

public sealed record AgentInstallProgress(AgentInstallStage Stage, long Done = 0, long Total = 0);

/// <summary>What an install did.</summary>
/// <param name="Installed">The agent now installed by the app (also when it already was).</param>
/// <param name="Problem">Why it failed.</param>
/// <param name="Detail">The failure in English.</param>
/// <param name="Release">The release found, when one was.</param>
/// <param name="AlreadyInstalled">That version was installed already: nothing downloaded.</param>
/// <param name="Downloaded">Bytes received in this call.</param>
public sealed record AgentInstallResult(
    ManagedAgent? Installed,
    AgentInstallProblem Problem = AgentInstallProblem.None,
    string? Detail = null,
    AgentRelease? Release = null,
    bool AlreadyInstalled = false,
    long Downloaded = 0)
{
    public bool Succeeded => Problem == AgentInstallProblem.None;

    /// <summary>Worth trying again later; a partial download resumes.</summary>
    public bool MayPass => Problem is AgentInstallProblem.Offline or AgentInstallProblem.Timeout or AgentInstallProblem.HttpError or AgentInstallProblem.Busy;
}

/// <summary>
/// Installs agents' official builds into the app's own folder (task 43; <see cref="AppPaths.Agents"/>), with no Node.js
/// and no admin rights: <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/</c> holds the build (Claude Code's program, Codex's
/// unpacked package), <c>&lt;id&gt;/installed.json</c> names the one in use and is written last (the commit point), so a
/// half-done install is never used. The release and its hash come from the agent's <see cref="IAgentDistribution"/>; the
/// download goes to <c>&lt;id&gt;/downloads/</c>, resumes after a dropped connection or a cancel (a <c>.part</c> file),
/// and is checked against the published size and SHA-256 (a mismatch is deleted) before anything is unpacked. Installing
/// another version is the update: the new one is committed first, then older version folders are removed (one still
/// running on Windows stays until the next install). One install per agent at a time (<c>&lt;id&gt;/install.lock</c>).
/// An agent the player installed (on <c>PATH</c>) is preferred by detection over this copy (<see cref="AgentPrograms"/>).
/// Never throws except for cancellation and unknown agent IDs.
/// </summary>
public sealed class AgentInstaller
{
    private const string StateFile = "installed.json";
    private const string DownloadsFolder = "downloads";
    private const string StagingPrefix = ".staging-";

    private readonly string _root;
    private readonly IReadOnlyDictionary<string, IAgentDistribution> _distributions;
    private readonly AgentPlatform? _platform;
    private readonly VerifiedDownloader _downloader;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <param name="root">The app's agents folder.</param>
    /// <param name="http">Downloads the builds (no automatic decompression: hashes are of the stored bytes).</param>
    /// <param name="distributions">Where each agent's builds come from.</param>
    /// <param name="platform">The platform to install for (<c>null</c> = a CPU no agent is built for).</param>
    public AgentInstaller(
        string root,
        HttpClient http,
        IEnumerable<IAgentDistribution> distributions,
        AgentPlatform? platform,
        TimeProvider? time = null,
        ILogger<AgentInstaller>? logger = null,
        PackFetchOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(distributions);
        _root = Path.GetFullPath(root);
        _distributions = distributions.ToDictionary(d => d.AgentId, StringComparer.OrdinalIgnoreCase);
        _platform = platform;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _downloader = new VerifiedDownloader(http, _time, _logger, options ?? new PackFetchOptions(TimeSpan.FromSeconds(60), 3, TimeSpan.FromSeconds(1)));
    }

    /// <summary>The real installer: Claude Code from downloads.claude.ai, Codex from GitHub, for this machine.</summary>
    public static AgentInstaller Create(AppPaths paths, OsEnvironment environment, HttpClient http, ILogger<AgentInstaller>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        return new AgentInstaller(
            paths.Agents,
            http,
            [new ClaudeCodeDistribution(http), new CodexDistribution(http)],
            AgentPlatform.Current(environment.Os),
            logger: logger);
    }

    /// <summary>An HTTP client for the downloads: no automatic decompression, no overall timeout (the downloader has its own).</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };

    public AgentPlatform? Platform => _platform;

    /// <summary>The agent the app installed, or <c>null</c> (none, or its state file or program is gone).</summary>
    public ManagedAgent? Find(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        var folder = AgentFolder(agentId);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, StateFile)));
            var root = document.RootElement;
            var version = root.GetProperty("version").GetString();
            var executable = root.GetProperty("executable").GetString();
            var installedAt = root.GetProperty("installedAt").GetDateTimeOffset();
            if (!AgentMetadata.IsVersion(version) || executable is null || !IsSafeRelative(executable))
            {
                return null;
            }

            var versionFolder = Path.Combine(folder, version!);
            var program = Path.Combine(versionFolder, executable.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(program) ? new ManagedAgent(agentId.ToLowerInvariant(), version!, versionFolder, program, installedAt) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The folder adapters should look in for the app's own copy (<c>null</c> when there is none).</summary>
    public string? ManagedDirectory(string agentId) => Find(agentId)?.ExecutableFolder;

    /// <summary>The release that <see cref="InstallAsync"/> would install now (the vendor's current one, or <paramref name="version"/>).</summary>
    public Task<AgentReleaseLookup> FindReleaseAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
    {
        var distribution = Distribution(agentId);
        return _platform is null
            ? Task.FromResult(AgentReleaseLookup.Fail(AgentInstallProblem.NoBuild, "No agent is built for this machine's CPU."))
            : distribution.FindAsync(_platform, version, cancellationToken);
    }

    /// <summary>Installs <paramref name="version"/> (else the vendor's current release), or updates the installed copy to it.</summary>
    public async Task<AgentInstallResult> InstallAsync(
        string agentId,
        string? version = null,
        IProgress<AgentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Distribution(agentId);
        progress?.Report(new AgentInstallProgress(AgentInstallStage.Resolving));
        var lookup = await FindReleaseAsync(agentId, version, cancellationToken).ConfigureAwait(false);
        if (lookup.Release is not { } release)
        {
            _logger.LogWarning("Agent {Agent}: no release to install ({Problem}: {Detail})", agentId, lookup.Problem, lookup.Detail);
            return new AgentInstallResult(null, lookup.Problem, lookup.Detail);
        }

        var folder = AgentFolder(agentId);
        FileStream lockFile;
        try
        {
            Directory.CreateDirectory(folder);
            lockFile = new FileStream(Path.Combine(folder, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (Directory.Exists(folder) && e is not DirectoryNotFoundException)
        {
            return new AgentInstallResult(null, AgentInstallProblem.Busy, $"Another install of {agentId} is running ({e.Message}).", release);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AgentInstallResult(null, AgentInstallProblem.DiskError, e.Message, release);
        }

        await using (lockFile.ConfigureAwait(false))
        {
            return await InstallLockedAsync(agentId, folder, release, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AgentInstallResult> InstallLockedAsync(string agentId, string folder, AgentRelease release, IProgress<AgentInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (Find(agentId) is { } existing && existing.Version == release.Version)
        {
            return new AgentInstallResult(existing, Release: release, AlreadyInstalled: true);
        }

        var download = Path.Combine(folder, DownloadsFolder, $"{release.Sha256[..16]}-{release.FileName}");
        var counter = new DownloadCounter(release.Size, (done, total) => progress?.Report(new AgentInstallProgress(AgentInstallStage.Downloading, done, total)));
        if (await _downloader.DownloadAsync(release.Url, download, release.Size, release.Sha256, counter, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            _logger.LogWarning("Agent {Agent} {Version} ({Url}): {Problem} ({Detail})", agentId, release.Version, release.Url, failure.Problem, failure.Detail);
            return new AgentInstallResult(null, Problem(failure.Problem), failure.Detail, release, Downloaded: counter.Downloaded);
        }

        progress?.Report(new AgentInstallProgress(AgentInstallStage.Unpacking));
        var staging = Path.Combine(folder, StagingPrefix + Path.GetRandomFileName());
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            var program = Path.Combine(staging, release.Executable.Replace('/', Path.DirectorySeparatorChar));
            if (release.Kind == AgentPackageKind.Binary)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(program)!);
                File.Copy(download, program);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(program, (UnixFileMode)0x1ED); // rwxr-xr-x
                }
            }
            else
            {
                AgentPackageArchive.Extract(download, staging);
            }

            if (!File.Exists(program))
            {
                TryDeleteFile(download);
                return new AgentInstallResult(null, AgentInstallProblem.ArchiveInvalid, $"The package has no {release.Executable}.", release, Downloaded: counter.Downloaded);
            }

            var target = Path.Combine(folder, release.Version);
            if (Directory.Exists(target))
            {
                // A folder no state file names: left by an install that stopped before its commit.
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            WriteState(folder, release);
            committed = true;
            _logger.LogInformation("Agent {Agent} {Version} installed into {Folder} from {Url} ({Verification}; SHA-256 checked)", agentId, release.Version, target, release.Url, release.Verification);
        }
        catch (AgentArchiveException e)
        {
            // The vendor's own file, hash checked, but not one the app can use: not kept for a retry.
            TryDeleteFile(download);
            return new AgentInstallResult(null, AgentInstallProblem.ArchiveInvalid, e.Message, release, Downloaded: counter.Downloaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AgentInstallResult(null, AgentInstallProblem.DiskError, e.Message, release, Downloaded: counter.Downloaded);
        }
        finally
        {
            if (!committed)
            {
                TryDeleteFolder(staging);
            }
        }

        TryDeleteFile(download);
        CleanUp(folder, release.Version);
        return new AgentInstallResult(Find(agentId), Release: release, Downloaded: counter.Downloaded);
    }

    /// <summary>Removes the app's copy of the agent (every version and download). A program still running keeps its files.</summary>
    /// <returns><c>null</c> when done (or nothing was installed), else why some of it is still there.</returns>
    public string? Uninstall(string agentId)
    {
        Distribution(agentId);
        var folder = AgentFolder(agentId);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            // The state file first: from then on nothing uses a half-removed copy.
            var state = Path.Combine(folder, StateFile);
            if (File.Exists(state))
            {
                File.Delete(state);
            }

            Directory.Delete(folder, recursive: true);
            _logger.LogInformation("Agent {Agent}: the app's copy removed", agentId);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Agent {Agent}: removing the app's copy failed", agentId);
            return e.Message;
        }
    }

    private IAgentDistribution Distribution(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return _distributions.TryGetValue(agentId, out var distribution)
            ? distribution
            : throw new ArgumentException($"No distribution for agent \"{agentId}\".", nameof(agentId));
    }

    private string AgentFolder(string agentId) => Path.Combine(_root, agentId.ToLowerInvariant());

    private void WriteState(string folder, AgentRelease release)
    {
        AtomicFile.Write(Path.Combine(folder, StateFile), stream =>
        {
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("version", release.Version);
            json.WriteString("executable", release.Executable);
            json.WriteString("installedAt", _time.GetUtcNow());
            json.WriteString("url", release.Url.AbsoluteUri);
            json.WriteString("sha256", release.Sha256);
            json.WriteString("verification", release.Verification);
            json.WriteEndObject();
        });
    }

    /// <summary>Older versions, stagings and downloads left behind; one in use stays for the next install.</summary>
    private void CleanUp(string folder, string keep)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(directory);
                if (name == keep)
                {
                    continue;
                }

                if (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || name == DownloadsFolder || AgentMetadata.IsVersion(name))
                {
                    TryDeleteFolder(directory);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(e, "Agent folder {Folder}: clean-up left for the next install", folder);
        }
    }

    private static AgentInstallProblem Problem(PackFetchProblem problem) => problem switch
    {
        PackFetchProblem.Offline => AgentInstallProblem.Offline,
        PackFetchProblem.Timeout => AgentInstallProblem.Timeout,
        PackFetchProblem.HttpError => AgentInstallProblem.HttpError,
        PackFetchProblem.FileMissing => AgentInstallProblem.NotFound,
        PackFetchProblem.HashMismatch => AgentInstallProblem.HashMismatch,
        _ => AgentInstallProblem.DiskError,
    };

    private static bool IsSafeRelative(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use (a running agent on Windows): removed by a later install.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left in downloads/: removed by the next install's clean-up.
        }
    }
}
