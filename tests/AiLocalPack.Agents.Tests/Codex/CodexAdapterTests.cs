using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Agents.Tests.Claude;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Codex;

/// <summary>Finding Codex, its sign-in probe and its run process (task 42).</summary>
public sealed class CodexAdapterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static AgentInstallation Installation => new("codex", "/opt/homebrew/bin/codex", "0.167.0", AgentSource.Path);

    private CodexAdapter Adapter(IProcessRunner runner, string? path = null) =>
        new(runner, new OsEnvironment(OsEnvironment.Current.Os, _dir.Combine("home"), name => name == "PATH" ? path : null), systemFolders: []);

    [Fact]
    public async Task Codex_on_path_is_found_with_its_version()
    {
        // Arrange
        var name = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        _dir.File(Path.Combine("bin", name), "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir.Combine(Path.Combine("bin", name)), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var adapter = Adapter(new RoutingProcessRunner(_ => ScriptedOutput.Of("codex-cli 0.167.0")), _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().Be(new AgentInstallation("codex", _dir.Combine(Path.Combine("bin", name)), "0.167.0", AgentSource.Path));
    }

    [Fact]
    public async Task The_sign_in_is_asked_with_login_status()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ScriptedOutput.Of("Logged in using ChatGPT"));

        // Act
        await Adapter(runner).GetAuthStatusAsync(Installation, TestContext.Current.CancellationToken);

        // Assert
        runner.Started.Single().Arguments.Should().Equal("login", "status");
    }

    [Fact]
    public async Task A_run_starts_in_its_folder_with_the_prompt_on_standard_input()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => CodexTranscripts.Output("success"));

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(Installation, new AgentRunRequest("Переведи.", _dir.Path), TestContext.Current.CancellationToken))
        {
        }

        // Assert
        var spec = runner.Started.Single();
        (spec.FileName, spec.Arguments[0], spec.WorkingDirectory, spec.StandardInput).Should().Be(("/opt/homebrew/bin/codex", "exec", _dir.Path, "Переведи."));
    }

    [Fact]
    public void A_chatgpt_sign_in_says_so()
    {
        // Act
        var status = CodexAdapter.ReadAuth(new ProcessResult(0, string.Empty, "Logged in using ChatGPT"));

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedIn, "ChatGPT"));
    }

    [Fact]
    public void An_api_key_sign_in_says_so()
    {
        // Act
        var status = CodexAdapter.ReadAuth(new ProcessResult(0, string.Empty, "Logged in using an API key - sk-proj-***ABCD"));

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedIn, "API key"));
    }

    [Fact]
    public void Not_logged_in_is_signed_out()
    {
        // Act
        var status = CodexAdapter.ReadAuth(new ProcessResult(1, string.Empty, "Not logged in"));

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedOut));
    }

    [Fact]
    public void Another_answer_is_unknown_in_its_words()
    {
        // Act
        var status = CodexAdapter.ReadAuth(new ProcessResult(127, string.Empty, "env: node: No such file or directory"));

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.Unknown, Detail: "The sign-in check exited with code 127: env: node: No such file or directory"));
    }

    [Fact]
    public void A_probe_that_timed_out_is_unknown()
    {
        // Act
        var status = CodexAdapter.ReadAuth(new ProcessResult(null, string.Empty, string.Empty));

        // Assert
        status.State.Should().Be(AgentAuthState.Unknown);
    }
}
