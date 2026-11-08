using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Install;

/// <summary>A CPU an agent build is made for.</summary>
public enum AgentCpu
{
    X64,
    Arm64,
}

/// <summary>
/// The OS and CPU an agent build must fit (task 43). Linux with the musl C library (Alpine) needs its own Claude Code
/// build; Codex's Linux builds are musl-linked and run on either.
/// </summary>
public sealed record AgentPlatform(OsKind Os, AgentCpu Cpu, bool Musl = false)
{
    /// <summary>
    /// This machine, or <c>null</c> on a CPU no agent is built for. <see cref="RuntimeInformation.OSArchitecture"/> gives
    /// the machine's own CPU even when the app runs emulated (Rosetta, Windows on Arm x64 emulation).
    /// </summary>
    public static AgentPlatform? Current(OsKind os)
    {
        AgentCpu cpu;
        switch (RuntimeInformation.OSArchitecture)
        {
            case Architecture.X64:
                cpu = AgentCpu.X64;
                break;
            case Architecture.Arm64:
                cpu = AgentCpu.Arm64;
                break;
            default:
                return null;
        }

        var musl = os == OsKind.Linux && (File.Exists("/lib/ld-musl-x86_64.so.1") || File.Exists("/lib/ld-musl-aarch64.so.1"));
        return new AgentPlatform(os, cpu, musl);
    }

    public override string ToString() =>
        (Os switch { OsKind.Windows => "windows", OsKind.MacOS => "macos", _ => "linux" })
        + "-" + (Cpu == AgentCpu.X64 ? "x64" : "arm64") + (Musl ? "-musl" : string.Empty);
}

/// <summary>How an agent's official build is shipped.</summary>
public enum AgentPackageKind
{
    /// <summary>The program itself (Claude Code's native build).</summary>
    Binary,

    /// <summary>A <c>.tar.gz</c> with the program and its helpers (Codex's package).</summary>
    TarGz,
}

/// <summary>One official build of an agent for one platform, with what its vendor publishes to check it.</summary>
/// <param name="AgentId">The adapter's ID.</param>
/// <param name="Version">The agent's version (<c>major.minor.patch[-pre]</c>).</param>
/// <param name="Url">The https file to download.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Sha256">Its SHA-256, lowercase hex.</param>
/// <param name="Kind">The program itself or a package to unpack.</param>
/// <param name="Executable">The program inside the install folder, relative, <c>/</c>-separated.</param>
/// <param name="Verification">How the hash itself was vouched for, in English (a signature, the vendor's checksum list).</param>
public sealed record AgentRelease(
    string AgentId,
    string Version,
    Uri Url,
    long Size,
    string Sha256,
    AgentPackageKind Kind,
    string Executable,
    string Verification)
{
    /// <summary>The downloaded file's name.</summary>
    public string FileName => Url.Segments[^1];
}

/// <summary>Why an agent couldn't be found or installed.</summary>
public enum AgentInstallProblem
{
    None,

    /// <summary>No connection (may pass).</summary>
    Offline,

    /// <summary>The server didn't answer in time (may pass).</summary>
    Timeout,

    /// <summary>The server answered with an error (a 5xx or a rate limit may pass).</summary>
    HttpError,

    /// <summary>No such version, or a file the release names is missing.</summary>
    NotFound,

    /// <summary>The version asked for isn't a version.</summary>
    BadVersion,

    /// <summary>The version is older than the app needs.</summary>
    TooOld,

    /// <summary>The vendor ships no build for this OS and CPU.</summary>
    NoBuild,

    /// <summary>The vendor's release description can't be read or contradicts itself.</summary>
    InvalidMetadata,

    /// <summary>The release description isn't signed by the vendor's pinned key.</summary>
    BadSignature,

    /// <summary>The download's size or SHA-256 isn't the published one (deleted).</summary>
    HashMismatch,

    /// <summary>The package can't be unpacked safely, or lacks the program.</summary>
    ArchiveInvalid,

    /// <summary>The app's folder can't be written.</summary>
    DiskError,

    /// <summary>Another install of the same agent is running.</summary>
    Busy,
}

/// <summary>A release found for the platform, or why none was.</summary>
public sealed record AgentReleaseLookup(AgentRelease? Release, AgentInstallProblem Problem = AgentInstallProblem.None, string? Detail = null)
{
    public static AgentReleaseLookup Fail(AgentInstallProblem problem, string detail) => new(null, problem, detail);
}

/// <summary>Where an agent's vendor publishes its official builds (task 43).</summary>
public interface IAgentDistribution
{
    /// <summary>The adapter's ID.</summary>
    string AgentId { get; }

    /// <summary>
    /// The build of <paramref name="version"/> (else the vendor's current release) for <paramref name="platform"/>, its hash
    /// vouched for by the vendor. Never throws except for cancellation.
    /// </summary>
    Task<AgentReleaseLookup> FindAsync(AgentPlatform platform, string? version, CancellationToken cancellationToken = default);
}

/// <summary>Small HTTP reads of release descriptions (one try, 15 s, size cap), shared by the distributions.</summary>
internal static partial class AgentMetadata
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>A version as vendors write it.</summary>
    public static bool IsVersion(string? text) => text is not null && Version().IsMatch(text);

    public static async Task<(byte[]? Body, AgentInstallProblem Problem, string? Detail)> GetAsync(
        HttpClient http,
        Uri url,
        int maxBytes,
        Action<HttpRequestMessage>? prepare,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            prepare?.Invoke(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var problem = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ? AgentInstallProblem.NotFound : AgentInstallProblem.HttpError;
                return (null, problem, $"{url}: HTTP {code} {response.ReasonPhrase}".TrimEnd());
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return (null, AgentInstallProblem.InvalidMetadata, $"{url}: larger than {maxBytes} bytes.");
            }

            var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        return (null, AgentInstallProblem.InvalidMetadata, $"{url}: larger than {maxBytes} bytes.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (buffer.ToArray(), AgentInstallProblem.None, null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, AgentInstallProblem.Timeout, $"{url}: no answer within {Timeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return (null, AgentInstallProblem.Offline, $"{url}: {e.Message}");
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$")]
    private static partial Regex Version();
}
