using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Install;

/// <summary>Installing an agent's official build into the app's folder (task 43).</summary>
public sealed class AgentInstallerTests : IDisposable
{
    private const string Host = "https://downloads.example/";

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _http = new();
    private readonly FakeDistribution _claude = new("claude");
    private readonly FakeDistribution _codex = new("codex");

    public void Dispose()
    {
        _dir.Dispose();
        _http.Dispose();
    }

    private string Root => _dir.Combine("agents");

    private AgentInstaller Installer() => new(
        Root,
        _http.CreateClient(),
        [_claude, _codex],
        new AgentPlatform(OsKind.MacOS, AgentCpu.Arm64),
        options: new PackFetchOptions(TimeSpan.FromSeconds(5), 1, TimeSpan.Zero));

    /// <summary>Publishes <paramref name="bytes"/> as the claude program of <paramref name="version"/>.</summary>
    private AgentRelease Binary(string version, byte[] bytes, string? sha256 = null)
    {
        var url = $"{Host}claude/{version}/claude";
        _http.OnGet(url, bytes);
        var release = new AgentRelease("claude", version, new Uri(url), bytes.Length, sha256 ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), AgentPackageKind.Binary, "claude", "test signature");
        _claude.Releases[version] = release;
        _claude.Current = release;
        return release;
    }

    /// <summary>Publishes a codex package of <paramref name="entries"/> (name → content; <c>null</c> = folder).</summary>
    private AgentRelease Package(string version, params (string Name, string? Content, TarEntryType Type)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content, type) in entries)
            {
                var entry = new PaxTarEntry(type, name) { Mode = (UnixFileMode)0x1ED };
                if (type == TarEntryType.SymbolicLink)
                {
                    entry.LinkName = content!;
                }
                else if (content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                }

                tar.WriteEntry(entry);
            }
        }

        var bytes = buffer.ToArray();
        var url = $"{Host}codex/{version}/codex-package.tar.gz";
        _http.OnGet(url, bytes);
        var release = new AgentRelease("codex", version, new Uri(url), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), AgentPackageKind.TarGz, "bin/codex", "test digests");
        _codex.Releases[version] = release;
        _codex.Current = release;
        return release;
    }

    private static (string, string?, TarEntryType) File_(string name, string content) => (name, content, TarEntryType.RegularFile);

    [Fact]
    public async Task A_binary_is_installed_into_its_version_folder()
    {
        // Arrange
        Binary("2.1.311", "#!/bin/sh\necho claude\n"u8.ToArray());

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Succeeded, result.Installed?.Executable).Should().Be((true, Path.Combine(Root, "claude", "2.1.311", "claude")));
    }

    [Fact]
    public async Task The_installed_agent_is_found_again_with_its_folder_for_detection()
    {
        // Arrange
        Binary("2.1.311", "program"u8.ToArray());
        await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var folder = Installer().ManagedDirectory("claude");

        // Assert
        folder.Should().Be(Path.Combine(Root, "claude", "2.1.311"));
    }

    public static bool IsUnix => !OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Unix permission bits.")]
    public async Task The_installed_binary_can_be_run()
    {
        // Arrange
        Binary("2.1.311", "program"u8.ToArray());

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (!OperatingSystem.IsWindows() && File.GetUnixFileMode(result.Installed!.Executable).HasFlag(UnixFileMode.UserExecute)).Should().BeTrue();
    }

    [Fact]
    public async Task A_download_with_another_hash_is_deleted_and_nothing_installed()
    {
        // Arrange
        Binary("2.1.311", "tampered"u8.ToArray(), sha256: new string('a', 64));
        var installer = Installer();

        // Act
        var result = await installer.InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Problem, installer.Find("claude"), Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("install.lock", StringComparison.Ordinal)).Count())
            .Should().Be((AgentInstallProblem.HashMismatch, (ManagedAgent?)null, 0));
    }

    [Fact]
    public async Task An_installed_version_is_not_downloaded_again()
    {
        // Arrange
        Binary("2.1.311", "program"u8.ToArray());
        await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);
        var requests = _http.Requests.Count;

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.AlreadyInstalled, _http.Requests.Count).Should().Be((true, requests));
    }

    [Fact]
    public async Task A_newer_release_updates_and_removes_the_old_version()
    {
        // Arrange
        Binary("2.1.311", "old"u8.ToArray());
        await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);
        Binary("2.1.320", "new"u8.ToArray());

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Installed?.Version, Directory.Exists(Path.Combine(Root, "claude", "2.1.311")), File.ReadAllText(result.Installed!.Executable))
            .Should().Be(("2.1.320", false, "new"));
    }

    [Fact]
    public async Task The_version_asked_for_is_the_one_installed()
    {
        // Arrange
        Binary("2.1.300", "older"u8.ToArray());
        Binary("2.1.311", "current"u8.ToArray());

        // Act
        var result = await Installer().InstallAsync("claude", "2.1.300", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Installed?.Version, _claude.Asked.Last()).Should().Be(("2.1.300", "2.1.300"));
    }

    [Fact]
    public async Task A_package_is_unpacked_whole_with_its_helpers()
    {
        // Arrange
        Package("0.167.0", File_("bin/codex", "codex"), File_("codex-path/rg", "rg"), File_("codex-package.json", "{}"));

        // Act
        var result = await Installer().InstallAsync("codex", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Installed?.ExecutableFolder, File.Exists(Path.Combine(Root, "codex", "0.167.0", "codex-path", "rg")))
            .Should().Be((Path.Combine(Root, "codex", "0.167.0", "bin"), true));
    }

    [Fact]
    public async Task A_package_entry_leaving_the_folder_refuses_the_package()
    {
        // Arrange
        Package("0.167.0", File_("bin/codex", "codex"), File_("../evil", "x"));
        var installer = Installer();

        // Act
        var result = await installer.InstallAsync("codex", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Problem, File.Exists(_dir.Combine("evil")), Directory.EnumerateDirectories(Path.Combine(Root, "codex")).Select(Path.GetFileName))
            .Should().BeEquivalentTo((AgentInstallProblem.ArchiveInvalid, false, new[] { "downloads" }));
    }

    [Fact]
    public async Task A_link_in_a_package_refuses_it()
    {
        // Arrange
        Package("0.167.0", File_("bin/codex", "codex"), ("bin/sh", "/bin/sh", TarEntryType.SymbolicLink));

        // Act
        var result = await Installer().InstallAsync("codex", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problem.Should().Be(AgentInstallProblem.ArchiveInvalid);
    }

    [Fact]
    public async Task A_package_without_the_program_is_refused()
    {
        // Arrange
        Package("0.167.0", File_("README.md", "no program"));
        var installer = Installer();

        // Act
        var result = await installer.InstallAsync("codex", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Problem, installer.Find("codex")).Should().Be((AgentInstallProblem.ArchiveInvalid, (ManagedAgent?)null));
    }

    [Fact]
    public async Task No_release_for_the_platform_is_passed_on()
    {
        // Arrange
        _claude.Failure = AgentReleaseLookup.Fail(AgentInstallProblem.NoBuild, "no build for linux-arm64");

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Problem, result.Detail).Should().Be((AgentInstallProblem.NoBuild, "no build for linux-arm64"));
    }

    [Fact]
    public async Task A_cancelled_download_resumes_where_it_stopped()
    {
        // Arrange
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var release = Binary("2.1.311", bytes);
        var part = Path.Combine(Root, "claude", "downloads", $"{release.Sha256[..16]}-claude.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllBytes(part, bytes[..100_000]);
        _http.On(HttpMethod.Get, release.Url.AbsoluteUri, request => new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[(int)request.Headers.Range!.Ranges.First().From!.Value..])
            {
                Headers = { ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(request.Headers.Range.Ranges.First().From!.Value, bytes.Length - 1, bytes.Length) },
            },
        });

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Succeeded, result.Downloaded).Should().Be((true, 100_000L));
    }

    [Fact]
    public void A_state_file_naming_a_missing_program_is_no_installation()
    {
        // Arrange
        _dir.File("agents/claude/installed.json", """{"schemaVersion": 1, "version": "2.1.311", "executable": "claude", "installedAt": "2026-11-08T12:00:00Z"}""");

        // Act
        var found = Installer().Find("claude");

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void A_state_file_with_an_escaping_path_is_no_installation()
    {
        // Arrange
        _dir.File("agents/claude/2.1.311/claude", "x");
        _dir.File("agents/claude/installed.json", """{"schemaVersion": 1, "version": "2.1.311", "executable": "../../x", "installedAt": "2026-11-08T12:00:00Z"}""");

        // Act
        var found = Installer().Find("claude");

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public async Task Uninstall_removes_the_apps_copy()
    {
        // Arrange
        Binary("2.1.311", "program"u8.ToArray());
        var installer = Installer();
        await installer.InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var problem = installer.Uninstall("claude");

        // Assert
        (problem, installer.Find("claude"), Directory.Exists(Path.Combine(Root, "claude"))).Should().Be(((string?)null, (ManagedAgent?)null, false));
    }

    [Fact]
    public async Task An_install_running_for_the_agent_makes_another_wait_its_turn()
    {
        // Arrange
        Binary("2.1.311", "program"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(Root, "claude"));
        await using var held = new FileStream(Path.Combine(Root, "claude", "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Act
        var result = await Installer().InstallAsync("claude", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problem.Should().Be(AgentInstallProblem.Busy);
    }

    [Fact]
    public async Task An_unknown_agent_is_a_programming_error()
    {
        // Act
        var act = () => Installer().InstallAsync("gemini", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeDistribution(string agentId) : IAgentDistribution
    {
        public string AgentId => agentId;

        public Dictionary<string, AgentRelease> Releases { get; } = [];

        public AgentRelease? Current { get; set; }

        public AgentReleaseLookup? Failure { get; set; }

        public List<string?> Asked { get; } = [];

        public Task<AgentReleaseLookup> FindAsync(AgentPlatform platform, string? version, CancellationToken cancellationToken = default)
        {
            Asked.Add(version);
            return Task.FromResult(Failure
                ?? (version is null ? new AgentReleaseLookup(Current) : Releases.TryGetValue(version, out var r) ? new AgentReleaseLookup(r) : AgentReleaseLookup.Fail(AgentInstallProblem.NotFound, version)));
        }
    }
}
