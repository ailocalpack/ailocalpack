using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Claude Code's official native builds (task 43), as Anthropic's own installer (<c>claude.ai/install.sh</c>,
/// <c>install.ps1</c>) gets them from <c>https://downloads.claude.ai/claude-code-releases</c>: the <c>stable</c> channel
/// file names the version (or the version asked for), <c>&lt;version&gt;/manifest.json</c> lists per platform the
/// program's name, size and SHA-256, and <c>&lt;version&gt;/&lt;platform&gt;/claude[.exe]</c> is the program.
/// <b>Checks</b> (decided): the manifest must carry a valid detached signature <c>manifest.json.sig</c> by Anthropic's
/// release key, pinned in the app (<see cref="ReleaseKeyFingerprint"/>, file in <c>Claude/Files</c>), before its hash
/// is trusted; the download is then checked against that size and SHA-256. Versions older than
/// <see cref="ClaudeCodeCommandLine.MinimumVersion"/> are refused. The <c>stable</c> channel (about a week behind
/// <c>latest</c>) is used, not <c>latest</c>, so the app drives builds that had time in the field.
/// </summary>
public sealed partial class ClaudeCodeDistribution : IAgentDistribution
{
    /// <summary>Anthropic's Claude Code release signing key (RSA 4096).</summary>
    public const string ReleaseKeyFingerprint = "31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE";

    /// <summary>The channel the app installs from.</summary>
    public const string Channel = "stable";

    private const int MaxManifestBytes = 1024 * 1024;

    private static readonly Lazy<OpenPgpPublicKey> PinnedKey = new(LoadPinnedKey);

    private readonly HttpClient _http;
    private readonly OpenPgpPublicKey _key;
    private readonly string _base;

    /// <param name="http">Downloads the channel file, manifest and signature.</param>
    /// <param name="key">The key the manifest must be signed with (default: Anthropic's, pinned).</param>
    /// <param name="baseUrl">The releases folder (default Anthropic's; tests pass their own).</param>
    public ClaudeCodeDistribution(HttpClient http, OpenPgpPublicKey? key = null, Uri? baseUrl = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _key = key ?? PinnedKey.Value;
        _base = (baseUrl ?? DefaultBaseUrl).AbsoluteUri.TrimEnd('/');
    }

    public static Uri DefaultBaseUrl { get; } = new("https://downloads.claude.ai/claude-code-releases");

    public string AgentId => ClaudeCodeAdapter.AgentId;

    /// <summary>Anthropic's name for a platform (<c>darwin-arm64</c>, <c>linux-x64-musl</c>, <c>win32-x64</c>).</summary>
    public static string PlatformName(AgentPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var cpu = platform.Cpu == AgentCpu.X64 ? "x64" : "arm64";
        return platform.Os switch
        {
            OsKind.Windows => $"win32-{cpu}",
            OsKind.MacOS => $"darwin-{cpu}",
            _ => $"linux-{cpu}" + (platform.Musl ? "-musl" : string.Empty),
        };
    }

    public async Task<AgentReleaseLookup> FindAsync(AgentPlatform platform, string? version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (version is null)
        {
            var (channel, problem, detail) = await AgentMetadata.GetAsync(_http, new Uri($"{_base}/{Channel}"), 256, null, cancellationToken).ConfigureAwait(false);
            if (channel is null)
            {
                return AgentReleaseLookup.Fail(problem, detail!);
            }

            version = Encoding.UTF8.GetString(channel).Trim();
            if (!AgentMetadata.IsVersion(version))
            {
                return AgentReleaseLookup.Fail(
                    AgentInstallProblem.InvalidMetadata,
                    "downloads.claude.ai didn't name a version (the service may be unreachable or not offered in this region).");
            }
        }
        else if (!AgentMetadata.IsVersion(version))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.BadVersion, $"\"{version}\" isn't a version (like 2.1.311).");
        }

        if (!ClaudeCodeCommandLine.IsSupported(version))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.TooOld, $"Claude Code {version} is older than {ClaudeCodeCommandLine.MinimumVersion}, which the app needs.");
        }

        var manifestUrl = new Uri($"{_base}/{version}/manifest.json");
        var (manifest, manifestProblem, manifestDetail) = await AgentMetadata.GetAsync(_http, manifestUrl, MaxManifestBytes, null, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return AgentReleaseLookup.Fail(manifestProblem, manifestDetail!);
        }

        var (signature, signatureProblem, signatureDetail) = await AgentMetadata.GetAsync(_http, new Uri(manifestUrl + ".sig"), 64 * 1024, null, cancellationToken).ConfigureAwait(false);
        if (signature is null)
        {
            return signatureProblem == AgentInstallProblem.NotFound
                ? AgentReleaseLookup.Fail(AgentInstallProblem.BadSignature, $"Claude Code {version}: the manifest isn't signed ({signatureDetail}).")
                : AgentReleaseLookup.Fail(signatureProblem, signatureDetail!);
        }

        var verification = OpenPgpSignature.Verify(manifest, Encoding.UTF8.GetString(signature), _key);
        if (!verification.IsValid)
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.BadSignature, $"Claude Code {version}: the manifest's signature by Anthropic's release key doesn't check out ({verification.Verdict}: {verification.Detail}).");
        }

        return Read(manifest, version, platform);
    }

    private AgentReleaseLookup Read(byte[] manifest, string version, AgentPlatform platform)
    {
        var name = PlatformName(platform);
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var stated) || stated.ValueKind != JsonValueKind.String || stated.GetString() != version)
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Claude Code {version}: the manifest is for another version.");
            }

            if (!root.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Object)
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Claude Code {version}: the manifest lists no platforms.");
            }

            if (!platforms.TryGetProperty(name, out var entry) || entry.ValueKind != JsonValueKind.Object)
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.NoBuild, $"Claude Code {version} has no build for {name}.");
            }

            var binary = entry.TryGetProperty("binary", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var expected = platform.Os == OsKind.Windows ? "claude.exe" : "claude";
            binary ??= expected;
            var checksum = entry.TryGetProperty("checksum", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var size = entry.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
            if (binary != expected || checksum is null || !Sha256().IsMatch(checksum) || size <= 0)
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Claude Code {version}: the manifest's {name} entry is damaged.");
            }

            return new AgentReleaseLookup(new AgentRelease(
                AgentId,
                version,
                new Uri($"{_base}/{version}/{name}/{binary}"),
                size,
                checksum,
                AgentPackageKind.Binary,
                binary,
                $"manifest signed by Anthropic's release key {_key.Fingerprint}"));
        }
        catch (JsonException e)
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Claude Code {version}: the manifest isn't JSON ({e.Message}).");
        }
    }

    private static OpenPgpPublicKey LoadPinnedKey()
    {
        using var stream = typeof(ClaudeCodeDistribution).Assembly.GetManifestResourceStream("AiLocalPack.Agents.Claude.claude-code-release-key.asc")
            ?? throw new InvalidOperationException("The Claude Code release key isn't embedded.");
        using var reader = new StreamReader(stream);
        var key = OpenPgpPublicKey.Read(reader.ReadToEnd());
        return key.Fingerprint == ReleaseKeyFingerprint
            ? key
            : throw new InvalidOperationException($"The embedded Claude Code release key is {key.Fingerprint}, not the pinned {ReleaseKeyFingerprint}.");
    }

    /// <summary>The pinned key (for tests and diagnostics).</summary>
    public static OpenPgpPublicKey ReleaseKey => PinnedKey.Value;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();
}
