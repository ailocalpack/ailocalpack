using System.Net;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Install;

/// <summary>Finding a Codex package from its GitHub release, its hash vouched for twice (task 43).</summary>
public sealed class CodexDistributionTests : IDisposable
{
    private const string Api = "https://api.example";
    private const string Hash = "af45ed45d940d7998809622c1c0296d3098ed7d7fad695284422c228bd454517";
    private const string Package = "codex-package-aarch64-apple-darwin.tar.gz";
    private const string Download = "https://github.com/openai/codex/releases/download/rust-v0.167.0/";

    private static readonly AgentPlatform MacArm = new(OsKind.MacOS, AgentCpu.Arm64);

    private readonly FakeHttpHandler _http = new();

    public void Dispose() => _http.Dispose();

    private static string Release(string tag = "rust-v0.167.0", string package = Package, string digest = "sha256:" + Hash, string? url = null) => $$"""
        {"tag_name": "{{tag}}", "assets": [
          {"name": "{{package}}", "size": 166340155, "digest": "{{digest}}", "browser_download_url": "{{url ?? Download + package}}"},
          {"name": "codex-package_SHA256SUMS", "size": 100, "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000", "browser_download_url": "{{Download}}codex-package_SHA256SUMS"}
        ]}
        """;

    private void Serve(string release, string sums, string path = "/repos/openai/codex/releases/latest")
    {
        _http.OnGet(Api + path, release);
        _http.OnGet(Download + "codex-package_SHA256SUMS", sums, mediaType: "text/plain");
    }

    private CodexDistribution Distribution() => new(_http.CreateClient(), new Uri(Api));

    [Fact]
    public async Task The_latest_release_gives_its_package_for_the_platform()
    {
        // Arrange
        Serve(Release(), $"{Hash}  {Package}\n0000  other\n");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Release.Should().BeEquivalentTo(new
        {
            Version = "0.167.0",
            Url = new Uri(Download + Package),
            Size = 166340155L,
            Sha256 = Hash,
            Kind = AgentPackageKind.TarGz,
            Executable = "bin/codex",
        });
    }

    [Fact]
    public async Task A_version_asked_for_reads_its_tag()
    {
        // Arrange
        Serve(Release(), $"{Hash}  {Package}\n", "/repos/openai/codex/releases/tags/rust-v0.167.0");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "0.167.0", TestContext.Current.CancellationToken);

        // Assert
        lookup.Release?.Version.Should().Be("0.167.0");
    }

    [Fact]
    public async Task The_api_is_asked_as_github_wants()
    {
        // Arrange
        Serve(Release(), $"{Hash}  {Package}\n");

        // Act
        await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        (_http.Requests[0].Header("User-Agent"), _http.Requests[0].Header("Accept")).Should().Be(("ailocalpack", "application/vnd.github+json"));
    }

    [Fact]
    public async Task A_checksum_list_disagreeing_with_githubs_digest_is_refused()
    {
        // Arrange
        Serve(Release(), $"{new string('b', 64)}  {Package}\n");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        (lookup.Release, lookup.Problem).Should().Be(((AgentRelease?)null, AgentInstallProblem.InvalidMetadata));
    }

    [Fact]
    public async Task A_checksum_list_without_the_package_is_refused()
    {
        // Arrange
        Serve(Release(), $"{Hash}  codex-package-x86_64-apple-darwin.tar.gz\n");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_package_without_a_digest_is_refused()
    {
        // Arrange
        Serve(Release(digest: "md5:abc"), $"{Hash}  {Package}\n");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_package_downloaded_from_elsewhere_is_refused()
    {
        // Arrange
        Serve(Release(url: "https://evil.example/" + Package), $"{Hash}  {Package}\n");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_release_without_the_platforms_package_has_no_build()
    {
        // Arrange
        Serve(Release(package: "codex-package-x86_64-unknown-linux-musl.tar.gz"), string.Empty);

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.NoBuild);
    }

    [Fact]
    public async Task A_release_without_a_version_tag_is_refused()
    {
        // Arrange
        Serve(Release(tag: "nightly"), string.Empty);

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_rate_limit_is_an_http_error_that_may_pass()
    {
        // Arrange
        _http.OnGet(Api + "/repos/openai/codex/releases/latest", HttpStatusCode.Forbidden);

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        (lookup.Problem, lookup.Detail!.Contains("rate limited", StringComparison.Ordinal)).Should().Be((AgentInstallProblem.HttpError, true));
    }

    [Fact]
    public async Task A_version_github_doesnt_know_is_not_found()
    {
        // Arrange
        _http.OnGet(Api + "/repos/openai/codex/releases/tags/rust-v9.9.9", HttpStatusCode.NotFound);

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "9.9.9", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.NotFound);
    }

    [Theory]
    [InlineData(OsKind.MacOS, AgentCpu.X64, "x86_64-apple-darwin")]
    [InlineData(OsKind.Linux, AgentCpu.Arm64, "aarch64-unknown-linux-musl")]
    [InlineData(OsKind.Windows, AgentCpu.X64, "x86_64-pc-windows-msvc")]
    public void Platforms_have_rust_targets(OsKind os, AgentCpu cpu, string expected)
    {
        // Act
        var target = CodexDistribution.Target(new AgentPlatform(os, cpu));

        // Assert
        target.Should().Be(expected);
    }

    [Fact]
    public async Task A_windows_build_runs_codex_exe()
    {
        // Arrange
        const string windows = "codex-package-x86_64-pc-windows-msvc.tar.gz";
        Serve(Release(package: windows), $"{Hash}  {windows}\n");

        // Act
        var lookup = await Distribution().FindAsync(new AgentPlatform(OsKind.Windows, AgentCpu.X64), null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Release?.Executable.Should().Be("bin/codex.exe");
    }
}
