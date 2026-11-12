using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>github login / whoami / logout</c> (task 58) against a local fake GitHub, secrets in a dev file.</summary>
public sealed class GitHubCommandTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeGitHubServer _github = new();

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private string SecretsFile => _dir.Combine("secrets.json");

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_SECRETS_FILE"] = SecretsFile,
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
            },
            ["github", .. args]);

    [Fact]
    public async Task Login_prints_the_code_and_where_to_enter_it()
    {
        // Act
        var result = await RunAsync("login", "--no-browser");

        // Assert
        result.StdOut.Should().Contain("Open https://github.com/login/device and enter the code: WDJB-MJHT");
    }

    [Fact]
    public async Task Login_signs_in_once_approved()
    {
        // Act
        var result = await RunAsync("login", "--no-browser");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain("Signed in to GitHub as octo (Octo Cat)");
    }

    [Fact]
    public async Task Login_when_signed_in_already_does_nothing()
    {
        // Arrange
        await RunAsync("login", "--no-browser");

        // Act
        var result = await RunAsync("login", "--no-browser");

        // Assert
        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Already signed in to GitHub as octo");
    }

    [Fact]
    public async Task Whoami_prints_the_signed_in_account()
    {
        // Arrange
        await RunAsync("login", "--no-browser");

        // Act
        var result = await RunAsync("whoami");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().StartWith("octo (Octo Cat)").And.Contain("scopes:   public_repo");
    }

    [Fact]
    public async Task Whoami_json_names_the_account_and_the_keychain()
    {
        // Arrange
        await RunAsync("login", "--no-browser");

        // Act
        var result = await RunAsync("whoami", "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        (json["state"]!.GetValue<string>(), json["login"]!.GetValue<string>(), json["keychain"]!.GetValue<string>())
            .Should().Be(("signedIn", "octo", "file"));
    }

    [Fact]
    public async Task Whoami_signed_out_exits_with_1()
    {
        // Act
        var result = await RunAsync("whoami");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Not signed in to GitHub");
    }

    [Fact]
    public async Task Logout_points_to_where_the_access_is_revoked_on_github()
    {
        // Arrange
        await RunAsync("login", "--no-browser");

        // Act
        var result = await RunAsync("logout");

        // Assert
        result.StdOut.Should().Contain($"{_github.Url}settings/connections/applications/");
    }
}
