using System.Globalization;
using AiLocalPack.Core.Activity;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Tests.Updates;
using AiLocalPack.Desktop.Updates;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.ViewModels.Settings.About;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>Settings → About (design §8, task 67): header, app updates, links, licenses.</summary>
public sealed class AboutSettingsSectionTests : IDisposable
{
    private readonly FakeUpdateSource _source = new();
    private readonly ServiceProvider _services;

    public AboutSettingsSectionTests()
    {
        _services = TestServices.Build(s => s.AddSingleton(_source));
    }

    public void Dispose() => _services.Dispose();

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private AboutSettingsSection Section => Get<AboutSettingsSection>();

    /// <summary>The section with <paramref name="version"/> found and downloaded.</summary>
    private async Task<AboutSettingsSection> ReadyAsync(string version = "0.5.0")
    {
        _source.Offer = version;
        var section = Section;
        await section.Updates.CheckCommand.ExecuteAsync(null);
        await Get<AppUpdater>().Download;
        return section;
    }

    [Fact]
    public void Header_names_the_app_and_its_version()
    {
        // Arrange
        _source.CurrentVersion = "0.4.2";

        // Act
        var header = Section.AboutHeader;

        // Assert
        header.Title.Should().Be("AI Local Pack 0.4.2");
    }

    [Fact]
    public void Header_names_the_stable_channel()
    {
        // Arrange
        _source.Channel = null;

        // Act
        var meta = Section.AboutHeader.Meta;

        // Assert
        meta.Should().StartWith("Stable channel · ").And.EndWith(" · © AI Local Pack contributors");
    }

    [Fact]
    public void Header_names_another_channel()
    {
        // Arrange
        _source.Channel = "beta";

        // Act
        var meta = Section.AboutHeader.Meta;

        // Assert
        meta.Should().StartWith("Channel: beta · ");
    }

    [Fact]
    public void Header_says_a_development_build()
    {
        // Arrange
        _source.Install = AppInstallKind.Development;

        // Act
        var meta = Section.AboutHeader.Meta;

        // Assert
        meta.Should().Contain("(development build)");
    }

    [Fact]
    public void Development_build_explains_that_it_doesnt_update()
    {
        // Arrange
        _source.Install = AppInstallKind.Development;

        // Act
        var row = Section.Updates;

        // Assert
        (row.Help, row.CanCheck, row.IsReady).Should().Be(("This copy wasn't installed from a release, so it doesn't update itself.", false, false));
    }

    [Fact]
    public void Updates_start_with_check_for_updates()
    {
        // Arrange
        var section = Section;

        // Act
        var row = section.Updates;

        // Assert
        (row.Help, row.CanCheck, row.CheckText).Should().Be(("Updates download in the background from GitHub Releases.", true, "Check for updates"));
    }

    [Fact]
    public async Task Check_with_nothing_newer_says_latest_with_the_time()
    {
        // Arrange
        using var services = TestServices.Build(s =>
        {
            s.AddSingleton(_source);
            s.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeSpan.Zero)));
        });
        var row = services.GetRequiredService<AboutSettingsSection>().Updates;

        // Act
        await row.CheckCommand.ExecuteAsync(null);

        // Assert
        // The culture's short time as ICU writes it (a narrow no-break space before "PM" on newer ICU).
        var time = new DateTime(2026, 11, 11, 14, 5, 0).ToString("t", CultureInfo.GetCultureInfo("en"));
        (row.Help, row.IsLatest, row.CheckText).Should().Be(($"You're on the latest version. Checked at {time}.", true, "Check again"));
    }

    [Fact]
    public async Task Downloaded_update_offers_restart_and_whats_new()
    {
        // Arrange
        var section = Section;

        // Act
        await ReadyAsync();

        // Assert
        (section.Updates.Help, section.Updates.IsReady, section.Updates.CanCheck, section.Updates.RestartCommand.CanExecute(null), section.Updates.WhatsNewCommand.CanExecute(null))
            .Should().Be(("0.5.0 is downloaded and ready. It also goes in the next time the app starts.", true, false, true, true));
    }

    [Fact]
    public async Task Downloading_update_shows_its_progress()
    {
        // Arrange
        _source.Offer = "0.5.0";
        _source.DownloadProgress = [40];
        _source.DownloadGate = new TaskCompletionSource();
        var row = Section.Updates;

        // Act
        await row.CheckCommand.ExecuteAsync(null);
        await WaitFor(() => row.Percent == 40);

        // Assert
        (row.Help, row.IsDownloading).Should().Be(("Downloading 0.5.0… 40 %", true));
        _source.DownloadGate.SetResult();
    }

    [Fact]
    public async Task Failed_check_says_so_and_offers_another()
    {
        // Arrange
        _source.CheckFailure = new HttpRequestException("offline");
        var row = Section.Updates;

        // Act
        await row.CheckCommand.ExecuteAsync(null);

        // Assert
        (row.IsFailed, row.CanCheck, row.Help).Should().Be((true, true, "Couldn't check for updates: GitHub Releases didn't answer. The app tries again later."));
    }

    [Fact]
    public async Task Section_list_hints_at_a_waiting_update()
    {
        // Arrange
        var section = Section;

        // Act
        await ReadyAsync();

        // Assert
        (section.Hint, section.HasBadge).Should().Be(("update", false));
    }

    [Fact]
    public void No_hint_without_an_update()
    {
        // Arrange
        var section = Section;

        // Act
        var hint = section.Hint;

        // Assert
        hint.Should().BeNull();
    }

    [Fact]
    public async Task Restart_to_update_restarts_the_app()
    {
        // Arrange
        var section = await ReadyAsync();

        // Act
        section.Updates.RestartCommand.Execute(null);

        // Assert
        (_source.Applied?.Version, Get<FakeAppQuit>().Quits, section.Updates.IsRestarting).Should().Be(("0.5.0", 1, true));
    }

    [Fact]
    public async Task Restart_during_an_install_says_why_it_waits()
    {
        // Arrange
        var section = await ReadyAsync();
        Get<ActivityStore>().SetTask(new ActivityTask("i", ActivityTaskKind.Install, null, "Alpha", "Installing", null, null, DateTimeOffset.UnixEpoch));

        // Act
        section.Updates.RestartCommand.Execute(null);

        // Assert
        (section.Updates.Error, Get<FakeAppQuit>().Quits).Should().Be(("A pack is being installed. Restart when it finishes.", 0));
    }

    [Fact]
    public async Task Restart_during_generation_asks_first()
    {
        // Arrange
        var section = await ReadyAsync();
        Get<ActivityStore>().SetTask(new ActivityTask("g", ActivityTaskKind.Generation, null, "Alpha", "Generating", null, null, DateTimeOffset.UnixEpoch));

        // Act
        section.Updates.RestartCommand.Execute(null);

        // Assert
        var confirm = Get<IDialogService>().Current.Should().BeOfType<ConfirmDialogViewModel>().Subject;
        (confirm.Title, confirm.ConfirmText, Get<FakeAppQuit>().Quits).Should().Be(("Restart to update now?", "Restart", 0));
    }

    [Fact]
    public async Task Confirmed_restart_during_generation_restarts()
    {
        // Arrange
        var section = await ReadyAsync();
        Get<ActivityStore>().SetTask(new ActivityTask("g", ActivityTaskKind.Generation, null, "Alpha", "Generating", null, null, DateTimeOffset.UnixEpoch));
        section.Updates.RestartCommand.Execute(null);

        // Act
        ((ConfirmDialogViewModel)Get<IDialogService>().Current!).ConfirmCommand.Execute(null);

        // Assert
        Get<FakeAppQuit>().Quits.Should().Be(1);
    }

    [Fact]
    public async Task Whats_new_opens_the_release_page()
    {
        // Arrange
        var section = await ReadyAsync();

        // Act
        section.Updates.WhatsNewCommand.Execute(null);

        // Assert
        Get<FakeShell>().OpenedUrls.Should().Equal(new Uri("https://github.com/ailocalpack/ailocalpack/releases/tag/v0.5.0"));
    }

    [Fact]
    public void Links_open_their_pages()
    {
        // Arrange
        var links = Section.Links;

        // Act
        foreach (var link in links)
        {
            link.OpenCommand.Execute(null);
        }

        // Assert
        Get<FakeShell>().OpenedUrls.Select(u => u.AbsoluteUri).Should().Equal(
            "https://github.com/ailocalpack/ailocalpack",
            "https://github.com/ailocalpack/registry",
            "https://github.com/ailocalpack/ailocalpack/issues/new",
            "https://github.com/ailocalpack/registry/blob/master/LEGAL.md");
    }

    [Fact]
    public void Source_link_shows_the_github_mark()
    {
        // Arrange
        var source = Section.Links[0];

        // Act
        var brand = source.IsBrand;

        // Assert
        (source.Label, source.Help, brand).Should().Be(("Source code", "ailocalpack/ailocalpack", true));
    }

    [Fact]
    public void Licenses_name_the_app_and_registry_licenses()
    {
        // Arrange
        var section = Section;

        // Act
        var help = section.Licenses.Help;

        // Assert
        help.Should().Be("App: Apache-2.0. Registry content: CC BY-SA 4.0.");
    }

    [Fact]
    public void Third_party_notices_open_in_a_dialog()
    {
        // Arrange
        var section = Section;

        // Act
        section.Licenses.NoticesCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeOfType<ThirdPartyNoticesDialogViewModel>()
            .Which.Text.Should().StartWith("AI Local Pack: third-party notices").And.Contain("Velopack 1.2.161");
    }

    [Fact]
    public void Search_finds_the_legal_page_by_its_address()
    {
        // Arrange
        var sections = Get<IEnumerable<SettingsSection>>();

        // Act
        var hits = SettingsSearch.Run<SettingsSection, SettingRow>(sections, "LEGAL.md");

        // Assert
        hits.Should().ContainSingle().Which.Rows.Should().ContainSingle().Which.Should().BeSameAs(Section.Links[3]);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
