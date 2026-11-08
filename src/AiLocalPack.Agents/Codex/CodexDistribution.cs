using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Codex;

/// <summary>
/// Codex's official standalone builds (task 43), as OpenAI's own installer (<c>install.sh</c> / <c>install.ps1</c> of
/// each release) gets them: the GitHub release of <c>openai/codex</c> (<c>releases/latest</c>, or tag
/// <c>rust-v&lt;version&gt;</c>, through the REST API), its asset <c>codex-package-&lt;target&gt;.tar.gz</c> (the program
/// <c>bin/codex[.exe]</c> with its helpers: <c>codex-path/rg</c>, <c>codex-resources/</c>…) kept whole, as the installer
/// does. <b>Checks</b> (decided): OpenAI signs no release description the app could pin (only Linux builds carry
/// Sigstore bundles), so the SHA-256 comes from two places that must agree: the asset's <c>digest</c> GitHub computed on
/// upload, and the line for it in the release's <c>codex-package_SHA256SUMS</c>; both are read over https from
/// github.com, and the package URL must be a download of that release. The download is checked against that size and hash.
/// </summary>
public sealed partial class CodexDistribution : IAgentDistribution
{
    public const string ChecksumAsset = "codex-package_SHA256SUMS";

    private const int MaxReleaseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _api;

    /// <param name="http">Reads the GitHub API and the checksum list.</param>
    /// <param name="apiBase">The GitHub API (default <c>https://api.github.com</c>; tests pass their own).</param>
    public CodexDistribution(HttpClient http, Uri? apiBase = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _api = (apiBase ?? new Uri("https://api.github.com")).AbsoluteUri.TrimEnd('/');
    }

    public string AgentId => CodexAdapter.AgentId;

    /// <summary>Where every Codex release asset is downloaded from.</summary>
    public static string DownloadPrefix => "https://github.com/openai/codex/releases/download/";

    /// <summary>The Rust target of a platform's build; Linux builds are musl-linked and run on glibc too.</summary>
    public static string Target(AgentPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var cpu = platform.Cpu == AgentCpu.X64 ? "x86_64" : "aarch64";
        return platform.Os switch
        {
            OsKind.Windows => $"{cpu}-pc-windows-msvc",
            OsKind.MacOS => $"{cpu}-apple-darwin",
            _ => $"{cpu}-unknown-linux-musl",
        };
    }

    public async Task<AgentReleaseLookup> FindAsync(AgentPlatform platform, string? version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (version is not null && !AgentMetadata.IsVersion(version))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.BadVersion, $"\"{version}\" isn't a version (like 0.167.0).");
        }

        var url = new Uri(version is null ? $"{_api}/repos/openai/codex/releases/latest" : $"{_api}/repos/openai/codex/releases/tags/rust-v{version}");
        var (body, problem, detail) = await AgentMetadata.GetAsync(_http, url, MaxReleaseBytes, GitHubHeaders, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return AgentReleaseLookup.Fail(problem, problem == AgentInstallProblem.HttpError ? detail + " (GitHub's API may be rate limited: try again in an hour)" : detail!);
        }

        string found;
        var assets = new Dictionary<string, (long Size, string? Digest, string? Url)>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var tag = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (tag is null || !tag.StartsWith("rust-v", StringComparison.Ordinal) || !AgentMetadata.IsVersion(tag[6..]))
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"The Codex release has no version tag ({tag ?? "none"}).");
            }

            found = tag[6..];
            if (version is not null && found != version)
            {
                return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"GitHub gave Codex {found} for {version}.");
            }

            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object && Text(asset, "name") is { } name)
                    {
                        var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                        assets.TryAdd(name, (size, Text(asset, "digest"), Text(asset, "browser_download_url")));
                    }
                }
            }
        }
        catch (JsonException e)
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"GitHub's answer isn't JSON ({e.Message}).");
        }

        var packageName = $"codex-package-{Target(platform)}.tar.gz";
        if (!assets.TryGetValue(packageName, out var package))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.NoBuild, $"Codex {found} has no {packageName}.");
        }

        if (!assets.TryGetValue(ChecksumAsset, out var sums) || !IsReleaseDownload(sums.Url, found, ChecksumAsset))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Codex {found} has no {ChecksumAsset}.");
        }

        if (package.Size <= 0 || package.Digest is not { } digest || !Digest().IsMatch(digest) || !IsReleaseDownload(package.Url, found, packageName))
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Codex {found}: GitHub gives no SHA-256 or no release download for {packageName}.");
        }

        var sha256 = digest[7..].ToLowerInvariant();
        var (list2, sumsProblem, sumsDetail) = await AgentMetadata.GetAsync(_http, new Uri(sums.Url!), 1024 * 1024, null, cancellationToken).ConfigureAwait(false);
        if (list2 is null)
        {
            return AgentReleaseLookup.Fail(sumsProblem == AgentInstallProblem.NotFound ? AgentInstallProblem.InvalidMetadata : sumsProblem, sumsDetail!);
        }

        var listed = ListedHash(Encoding.UTF8.GetString(list2), packageName);
        if (listed is null)
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Codex {found}: {ChecksumAsset} doesn't list {packageName}.");
        }

        if (listed != sha256)
        {
            return AgentReleaseLookup.Fail(AgentInstallProblem.InvalidMetadata, $"Codex {found}: {ChecksumAsset} gives {listed} for {packageName}, GitHub {sha256}.");
        }

        return new AgentReleaseLookup(new AgentRelease(
            AgentId,
            found,
            new Uri(package.Url!),
            package.Size,
            sha256,
            AgentPackageKind.TarGz,
            platform.Os == OsKind.Windows ? "bin/codex.exe" : "bin/codex",
            $"SHA-256 from GitHub's asset digest, matching the release's {ChecksumAsset}"));
    }

    private static void GitHubHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", "ailocalpack");
    }

    private static bool IsReleaseDownload(string? url, string version, string name) =>
        url == $"{DownloadPrefix}rust-v{version}/{name}";

    /// <summary>The hash a <c>sha256sum</c> list gives <paramref name="name"/>, lowercase, or <c>null</c>.</summary>
    internal static string? ListedHash(string list, string name)
    {
        foreach (var line in list.ReplaceLineEndings("\n").Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == name && SumHash().IsMatch(parts[0]))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex Digest();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex SumHash();
}
