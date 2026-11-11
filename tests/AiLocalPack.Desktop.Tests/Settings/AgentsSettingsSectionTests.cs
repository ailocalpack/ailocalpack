using AiLocalPack.Agents;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class AgentsSettingsSectionTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private FakeAgentHost Host => _services.GetRequiredService<FakeAgentHost>();

    private SettingsStore Store => _services.GetRequiredService<SettingsStore>();

    /// <summary>The section, created (and so checked) after the test set the agents up.</summary>
    private AgentsSettingsSection Section() => _services.GetRequiredService<AgentsSettingsSection>();

    private AgentRow Row(string id) => Section().Agents.Single(a => a.AgentId == id);

    private static AgentInstallResult Installed(string id, string version = "2.1.319") =>
        new(new ManagedAgent(id, version, $"/data/agents/{id}/{version}", $"/data/agents/{id}/{version}/{id}", DateTimeOffset.UnixEpoch));

    [Fact]
    public void Lists_one_row_per_agent()
    {
        // Act
        var section = Section();

        // Assert
        section.Agents.Select(a => a.Label).Should().Equal("Claude Code", "Codex");
    }

    [Fact]
    public void A_missing_agent_offers_install_and_is_never_a_warning()
    {
        // Act
        var section = Section();

        // Assert
        section.Agents.Select(a => (a.State, a.StatusText, a.CanInstall)).Should().AllBeEquivalentTo((AgentRowState.NotInstalled, "Not installed", true));
        section.BadgeCount.Should().Be(0);
    }

    [Fact]
    public void The_install_explanation_names_the_download_size_once_the_release_is_known()
    {
        // Arrange
        Host.Releases["claude"] = FakeAgentHost.Release("claude", "2.1.320", 58 * 1024 * 1024);

        // Act
        var help = Row("claude").Help;

        // Assert
        help.Should().Be("Downloads Anthropic's official build (58 MB) into the app's folder and checks its signature. No Node.js, no admin rights.");
    }

    [Fact]
    public void A_signed_in_agent_found_on_the_system_is_ready_with_its_source_version_and_account()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude");

        // Act
        var row = Row("claude");

        // Assert
        (row.State, row.StatusText, row.SourceText, row.CanSignOut).Should().Be((
            AgentRowState.Ready,
            "Ready · signed in",
            "Found on your system: /usr/local/bin/claude · v2.1.319 · signed in as player@example.com",
            true));
    }

    [Fact]
    public void A_ready_agent_is_remembered_as_signed_in()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");

        // Act
        _ = Section();

        // Assert
        Store.Current.SignedInAgents.Should().Equal("codex");
    }

    [Fact]
    public void An_agent_remembered_as_signed_in_and_now_signed_out_is_an_expired_session_alert()
    {
        // Arrange
        Store.Update(s => s with { SignedInAgents = ["claude"] });
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");

        // Act
        var section = Section();

        // Assert
        (section.BadgeCount, section.Alerts.Single().Title, section.Alerts.Single().ActionText).Should().Be((1, "Claude Code is signed out", "Sign in"));
    }

    [Fact]
    public void A_never_signed_in_agent_offers_sign_in_without_a_warning()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");

        // Act
        var section = Section();

        // Assert
        (Row("claude").StatusText, Row("claude").CanSignIn, section.BadgeCount).Should().Be(("Installed · not signed in", true, 0));
    }

    [Fact]
    public void Install_shows_the_download_percent_while_it_runs()
    {
        // Arrange
        var finish = new TaskCompletionSource<AgentInstallResult>();
        Host.Install = (_, progress, _) =>
        {
            progress!.Report(new AgentInstallProgress(AgentInstallStage.Downloading, 40, 100));
            return finish.Task;
        };
        var row = Row("codex");

        // Act
        _ = row.InstallCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.StatusText, row.Help, row.CanCancel).Should().Be((AgentRowState.Installing, "Installing · 40%", "Downloading from OpenAI…", true));
        finish.SetResult(Installed("codex"));
    }

    [Fact]
    public async Task A_finished_install_checks_again_and_asks_for_sign_in()
    {
        // Arrange
        Host.Install = (id, _, _) =>
        {
            Host.Probes[id] = FakeAgentHost.SignedOut(id, AgentSource.Managed, "0.167.0");
            return Task.FromResult(Installed(id, "0.167.0"));
        };
        var row = Row("codex");

        // Act
        await row.InstallCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.SourceText, row.HasError).Should().Be((AgentRowState.NotSignedIn, "Installed by AI Local Pack · v0.167.0", false));
    }

    [Fact]
    public async Task A_failed_install_says_why_and_offers_install_again()
    {
        // Arrange
        Host.Install = (_, _, _) => Task.FromResult(new AgentInstallResult(null, AgentInstallProblem.HashMismatch, "size differs"));
        var row = Row("claude");

        // Act
        await row.InstallCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.Error).Should().Be((AgentRowState.NotInstalled, "The download didn't pass the signature or hash check and was deleted. Nothing was installed."));
    }

    [Fact]
    public async Task Cancelling_an_install_goes_back_to_not_installed_without_an_error()
    {
        // Arrange
        Host.Install = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Installed("claude");
        };
        var row = Row("claude");
        var install = row.InstallCommand.ExecuteAsync(null);

        // Act
        row.CancelCommand.Execute(null);

        // Assert
        await install;
        (row.State, row.HasError).Should().Be((AgentRowState.NotInstalled, false));
    }

    [Fact]
    public void Sign_in_waits_for_the_browser_with_cancel()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");
        var finish = new TaskCompletionSource<AgentSignInResult>();
        Host.SignIn = (_, _) => finish.Task;
        var row = Row("claude");

        // Act
        _ = row.SignInCommand.ExecuteAsync(null);

        // Assert
        (row.StatusText, row.Help, row.CanCancel, row.CanSignIn).Should().Be(("Waiting for sign-in", "Finish signing in in the browser window Claude Code opened.", true, false));
        finish.SetResult(new AgentSignInResult(AgentSignInOutcome.Cancelled, null));
    }

    [Fact]
    public async Task A_finished_sign_in_makes_the_agent_ready_and_remembers_it()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");
        Host.SignIn = (_, _) => Task.FromResult(new AgentSignInResult(AgentSignInOutcome.SignedIn, new AgentAuthStatus(AgentAuthState.SignedIn, "me@example.com")));
        var row = Row("claude");

        // Act
        await row.SignInCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.SourceText, Store.Current.SignedInAgents?.Single()).Should().Be((
            AgentRowState.Ready, "Found on your system: /usr/local/bin/claude · v2.1.319 · signed in as me@example.com", "claude"));
    }

    [Fact]
    public async Task A_sign_in_that_ended_without_signing_in_says_so()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedOut("codex");
        var row = Row("codex");

        // Act
        await row.SignInCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.Error).Should().Be((AgentRowState.NotSignedIn, "The sign-in ended without signing in. Try again."));
    }

    [Fact]
    public async Task Signing_in_settles_an_expired_session_alert()
    {
        // Arrange
        Store.Update(s => s with { SignedInAgents = ["claude"] });
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");
        Host.SignIn = (_, _) => Task.FromResult(new AgentSignInResult(AgentSignInOutcome.SignedIn, new AgentAuthStatus(AgentAuthState.SignedIn)));
        var section = Section();

        // Act
        await Row("claude").SignInCommand.ExecuteAsync(null);

        // Assert
        (section.BadgeCount, section.Alerts.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task Signing_out_here_forgets_the_agent_so_it_isnt_an_expired_session()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude");
        var section = Section();

        // Act
        await Row("claude").SignOutCommand.ExecuteAsync(null);

        // Assert
        (Row("claude").State, section.BadgeCount, Store.Current.SignedInAgents).Should().Be((AgentRowState.NotSignedIn, 0, (IReadOnlyList<string>?)null));
    }

    [Fact]
    public void The_apps_own_copy_offers_a_newer_release_as_an_update()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude", AgentSource.Managed, "2.1.311");
        Host.Releases["claude"] = FakeAgentHost.Release("claude", "2.1.319");

        // Act
        var row = Row("claude");

        // Assert
        (row.CanUpdate, row.UpdateText).Should().Be((true, "Update to 2.1.319"));
    }

    [Fact]
    public void The_players_own_copy_is_never_offered_an_update()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude", AgentSource.Path, "2.1.311");
        Host.Releases["claude"] = FakeAgentHost.Release("claude", "2.1.319");

        // Act
        var row = Row("claude");

        // Assert
        (row.CanUpdate, Host.Calls.Contains("release claude")).Should().Be((false, false));
    }

    [Fact]
    public void An_update_shows_its_version_and_percent()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude", AgentSource.Managed, "2.1.311");
        Host.Releases["claude"] = FakeAgentHost.Release("claude", "2.1.319");
        var finish = new TaskCompletionSource<AgentInstallResult>();
        Host.Install = (_, progress, _) =>
        {
            progress!.Report(new AgentInstallProgress(AgentInstallStage.Downloading, 1, 4));
            return finish.Task;
        };
        var row = Row("claude");

        // Act
        _ = row.UpdateCommand.ExecuteAsync(null);

        // Assert
        (row.State, row.StatusText).Should().Be((AgentRowState.Updating, "Updating to 2.1.319 · 25%"));
        finish.SetResult(Installed("claude", "2.1.319"));
    }

    [Fact]
    public void A_too_old_copy_of_the_player_asks_them_to_update_it()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude", version: "2.1.100") with { IsSupported = false };

        // Act
        var row = Row("claude");

        // Assert
        (row.StatusText, row.Help, row.CanCheckAgain, row.CanSignOut).Should().Be((
            "Version 2.1.100 is too old",
            "AI Local Pack needs version 2.1.259 or later. Update your copy the way you installed it, then check again.",
            true,
            false));
    }

    [Fact]
    public void A_sign_in_that_couldnt_be_checked_shows_the_agents_words()
    {
        // Arrange
        Host.Probes["codex"] = new AgentProbe(FakeAgentHost.Program("codex"), true, new AgentAuthStatus(AgentAuthState.Unknown, Detail: "codex login status timed out"));

        // Act
        var row = Row("codex");

        // Assert
        (row.StatusText, row.Help, row.CanSignIn, row.CanCheckAgain).Should().Be((
            "Sign-in not checked", "Couldn't check the sign-in: codex login status timed out", true, true));
    }

    [Fact]
    public void Opening_the_section_checks_the_agents_again()
    {
        // Arrange
        var section = Section();
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");

        // Act
        section.OnShown();

        // Assert
        Row("codex").State.Should().Be(AgentRowState.Ready);
    }

    [Fact]
    public void No_ready_agent_shows_no_default_choice()
    {
        // Act
        var row = Section().DefaultAgent;

        // Assert
        (row.HasReadyAgent, row.SelectedChoice, row.NoAgentText).Should().Be((false, (AgentChoice?)null, "Install and sign in to an agent first"));
    }

    [Fact]
    public void The_first_ready_agent_is_the_default_until_one_is_picked()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");

        // Act
        var row = Section().DefaultAgent;

        // Assert
        (row.SelectedChoice?.Agent.AgentId, row.Choices.Select(c => c.IsEnabled)).Should().BeEquivalentTo(("codex", new[] { false, true }));
    }

    [Fact]
    public void Picking_a_ready_agent_stores_it_as_the_default()
    {
        // Arrange
        Host.Probes["claude"] = FakeAgentHost.SignedIn("claude");
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");
        var row = Section().DefaultAgent;

        // Act
        row.SelectedChoice = row.Choices[1];

        // Assert
        Store.Current.DefaultAgent.Should().Be("codex");
    }

    [Fact]
    public void An_agent_that_isnt_ready_cant_be_picked()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");
        var row = Section().DefaultAgent;

        // Act
        row.SelectedChoice = row.Choices[0];

        // Assert
        (row.SelectedChoice?.Agent.AgentId, Store.Current.DefaultAgent).Should().Be(("codex", (string?)null));
    }

    [Fact]
    public void A_stored_default_that_is_signed_out_gives_way_to_a_ready_one()
    {
        // Arrange
        Store.Update(s => s with { DefaultAgent = "claude" });
        Host.Probes["claude"] = FakeAgentHost.SignedOut("claude");
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex");

        // Act
        var row = Section().DefaultAgent;

        // Assert
        row.SelectedChoice?.Agent.AgentId.Should().Be("codex");
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "codex")]
    [InlineData(null, "codex")]
    public void Resolve_keeps_a_ready_stored_default_else_takes_the_first_ready(string? setting, string expected)
    {
        // Act
        var id = DefaultAgentRow.Resolve(setting, ["codex"]);

        // Assert
        id.Should().Be(expected);
    }

    [Fact]
    public void The_section_explains_what_agents_are_for()
    {
        // Act
        var lead = Section().Lead;

        // Assert
        lead.Should().StartWith("Agents are only used to create new translations with AI");
    }
}
