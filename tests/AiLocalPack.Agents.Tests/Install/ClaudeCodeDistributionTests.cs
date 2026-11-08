using System.Net;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Install;

/// <summary>Finding a Claude Code build from Anthropic's signed release manifest (task 43).</summary>
public sealed class ClaudeCodeDistributionTests : IDisposable
{
    private const string Base = "https://downloads.example/claude-code-releases";
    private const string Hash = "1f53587e92dee8e089fa349188a4cda64fa34ace6e7b0ca2ee278ddaa3cc2ebd";

    private static readonly AgentPlatform MacArm = new(OsKind.MacOS, AgentCpu.Arm64);

    private readonly TestPgp _key = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose()
    {
        _key.Dispose();
        _http.Dispose();
    }

    private static string Manifest(string version = "2.1.311", string platform = "darwin-arm64", string binary = "claude") =>
        "{\"version\": \"" + version + "\", \"platforms\": {\"" + platform + "\": {\"binary\": \"" + binary + "\", \"checksum\": \"" + Hash + "\", \"size\": 1234}}}";

    private ClaudeCodeDistribution Distribution() => new(_http.CreateClient(), _key.PublicKey, new Uri(Base));

    private void Publish(string version, string manifest, string? signature = null)
    {
        _http.OnGet($"{Base}/{version}/manifest.json", manifest);
        _http.OnGet($"{Base}/{version}/manifest.json.sig", signature ?? _key.Sign(manifest), mediaType: "text/plain");
    }

    [Fact]
    public async Task The_stable_channel_names_the_version_and_the_signed_manifest_the_build()
    {
        // Arrange
        _http.OnGet($"{Base}/stable", "2.1.311\n", mediaType: "text/plain");
        Publish("2.1.311", Manifest());

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Release.Should().BeEquivalentTo(new
        {
            Version = "2.1.311",
            Url = new Uri($"{Base}/2.1.311/darwin-arm64/claude"),
            Size = 1234L,
            Sha256 = Hash,
            Kind = AgentPackageKind.Binary,
            Executable = "claude",
        });
    }

    [Fact]
    public async Task A_version_asked_for_skips_the_channel()
    {
        // Arrange
        Publish("2.1.300", Manifest("2.1.300"));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.300", TestContext.Current.CancellationToken);

        // Assert
        (lookup.Release?.Version, _http.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/stable", StringComparison.Ordinal))).Should().Be(("2.1.300", false));
    }

    [Fact]
    public async Task A_manifest_signed_by_another_key_is_refused()
    {
        // Arrange
        using var other = new TestPgp();
        Publish("2.1.311", Manifest(), other.Sign(Manifest()));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        (lookup.Release, lookup.Problem).Should().Be(((AgentRelease?)null, AgentInstallProblem.BadSignature));
    }

    [Fact]
    public async Task A_manifest_changed_after_signing_is_refused()
    {
        // Arrange
        Publish("2.1.311", Manifest().Replace("1234", "9999", StringComparison.Ordinal), _key.Sign(Manifest()));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.BadSignature);
    }

    [Fact]
    public async Task An_unsigned_manifest_is_refused()
    {
        // Arrange
        _http.OnGet($"{Base}/2.1.311/manifest.json", Manifest());
        _http.OnGet($"{Base}/2.1.311/manifest.json.sig", HttpStatusCode.NotFound);

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.BadSignature);
    }

    [Fact]
    public async Task A_platform_the_manifest_lacks_has_no_build()
    {
        // Arrange
        Publish("2.1.311", Manifest(platform: "linux-x64"));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.NoBuild);
    }

    [Fact]
    public async Task A_program_name_with_a_path_is_refused()
    {
        // Arrange
        Publish("2.1.311", Manifest(binary: "../claude"));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_manifest_of_another_version_is_refused()
    {
        // Arrange
        Publish("2.1.311", Manifest("2.1.300"));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.311", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task A_channel_answering_with_a_page_is_no_version()
    {
        // Arrange
        _http.OnGet($"{Base}/stable", "<html>Not available in your region</html>", mediaType: "text/html");

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.InvalidMetadata);
    }

    [Fact]
    public async Task No_connection_is_offline()
    {
        // Arrange
        _http.On(HttpMethod.Get, $"{Base}/stable", _ => throw new HttpRequestException("no route to host"));

        // Act
        var lookup = await Distribution().FindAsync(MacArm, null, TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.Offline);
    }

    [Fact]
    public async Task A_version_older_than_the_app_needs_is_refused_before_any_download()
    {
        // Act
        var lookup = await Distribution().FindAsync(MacArm, "2.1.100", TestContext.Current.CancellationToken);

        // Assert
        (lookup.Problem, _http.Requests.Count).Should().Be((AgentInstallProblem.TooOld, 0));
    }

    [Fact]
    public async Task Text_that_is_no_version_is_refused()
    {
        // Act
        var lookup = await Distribution().FindAsync(MacArm, "latest; rm -rf", TestContext.Current.CancellationToken);

        // Assert
        lookup.Problem.Should().Be(AgentInstallProblem.BadVersion);
    }

    [Fact]
    public async Task The_real_signed_manifest_gives_the_real_build()
    {
        // Arrange
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "agents", "claude-releases");
        _http.OnGet($"{Base}/2.1.319/manifest.json", File.ReadAllBytes(Path.Combine(fixtures, "manifest-2.1.319.json")));
        _http.OnGet($"{Base}/2.1.319/manifest.json.sig", File.ReadAllBytes(Path.Combine(fixtures, "manifest-2.1.319.json.sig")));
        var distribution = new ClaudeCodeDistribution(_http.CreateClient(), baseUrl: new Uri(Base));

        // Act
        var lookup = await distribution.FindAsync(new AgentPlatform(OsKind.Windows, AgentCpu.X64), "2.1.319", TestContext.Current.CancellationToken);

        // Assert
        (lookup.Release?.Url.AbsoluteUri, lookup.Release?.Sha256, lookup.Release?.Size).Should().Be((
            $"{Base}/2.1.319/win32-x64/claude.exe",
            "2e055732c9d03d22700d9046251a8370b673607bfcc9e4d16a1b0e8fb1345cbd",
            344807072L));
    }

    [Theory]
    [InlineData(OsKind.MacOS, AgentCpu.X64, false, "darwin-x64")]
    [InlineData(OsKind.Linux, AgentCpu.Arm64, false, "linux-arm64")]
    [InlineData(OsKind.Linux, AgentCpu.X64, true, "linux-x64-musl")]
    [InlineData(OsKind.Windows, AgentCpu.Arm64, false, "win32-arm64")]
    public void Platforms_have_anthropics_names(OsKind os, AgentCpu cpu, bool musl, string expected)
    {
        // Act
        var name = ClaudeCodeDistribution.PlatformName(new AgentPlatform(os, cpu, musl));

        // Assert
        name.Should().Be(expected);
    }
}
