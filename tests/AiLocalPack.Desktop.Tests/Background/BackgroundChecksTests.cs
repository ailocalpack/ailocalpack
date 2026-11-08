using AiLocalPack.Core.Background;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Background;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Background;

/// <summary>General → Background applied (task 39): the autostart entry and the background re-check.</summary>
public sealed class BackgroundChecksTests
{
    private static readonly AutostartCommand Command = new("/Apps/AiLocalPack", ["--background"]);

    private static ServiceProvider Services(AutostartCommand? command = null, FakeClock? clock = null) => TestServices.Build(s =>
    {
        s.AddSingleton(new AutostartTarget(command ?? Command));
        s.AddSingleton<TimeProvider>(clock ?? new FakeClock());
    });

    [Fact]
    public void Start_registers_the_app_to_start_with_the_system()
    {
        // Arrange
        using var services = Services();

        // Act
        services.GetRequiredService<BackgroundChecks>().Start();

        // Assert
        services.GetRequiredService<FakeAutostart>().Content.Should().Be("/Apps/AiLocalPack --background");
    }

    [Fact]
    public void Turning_the_setting_off_removes_the_entry()
    {
        // Arrange
        using var services = Services();
        services.GetRequiredService<BackgroundChecks>().Start();

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { RunInBackground = false });

        // Assert
        services.GetRequiredService<FakeAutostart>().Content.Should().BeNull();
    }

    [Fact]
    public void Other_setting_changes_leave_the_entry_alone()
    {
        // Arrange
        using var services = Services();
        services.GetRequiredService<BackgroundChecks>().Start();

        // Act
        services.GetRequiredService<SettingsStore>().Update(s => s with { Theme = AppTheme.Dark });

        // Assert
        services.GetRequiredService<FakeAutostart>().Writes.Should().Be(1);
    }

    [Fact]
    public void Throwaway_home_never_touches_the_entry()
    {
        // Arrange
        using var services = TestServices.Build(s => s.AddSingleton(new AutostartTarget(null)));

        // Act
        services.GetRequiredService<BackgroundChecks>().Start();

        // Assert
        services.GetRequiredService<FakeAutostart>().Writes.Should().Be(0);
    }

    [Fact]
    public async Task Steam_manifest_change_makes_a_recheck_due_after_the_quiet_time()
    {
        // Arrange
        var clock = new FakeClock();
        using var steam = new TempDir();
        using var services = TestServices.Build(
            s =>
            {
                s.AddSingleton(new AutostartTarget(Command));
                s.AddSingleton<TimeProvider>(clock);
            },
            fixture => fixture.AddGame(10, "Alpha"));
        var checks = services.GetRequiredService<BackgroundChecks>();
        checks.Start();
        var manifest = Directory.GetFiles(services.GetRequiredService<Core.Steam.SteamManifestWatcher>().Folders.Single(), "appmanifest_*.acf").Single();
        await File.AppendAllTextAsync(manifest, "\n", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => { clock.Advance(TimeSpan.FromMinutes(5)); return checks.Scheduler.IsDue; });

        // Act
        var ran = await checks.Scheduler.TickAsync(TestContext.Current.CancellationToken);

        // Assert
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task Manifest_changes_are_ignored_with_the_tray_off()
    {
        // Arrange
        var clock = new FakeClock();
        using var services = TestServices.Build(
            s =>
            {
                s.AddSingleton(new AutostartTarget(Command));
                s.AddSingleton<TimeProvider>(clock);
            },
            fixture => fixture.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { RunInBackground = false });
        var checks = services.GetRequiredService<BackgroundChecks>();
        checks.Start();
        var manifest = Directory.GetFiles(services.GetRequiredService<Core.Steam.SteamManifestWatcher>().Folders.Single(), "appmanifest_*.acf").Single();

        // Act
        await File.AppendAllTextAsync(manifest, "\n", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        // Assert
        checks.Scheduler.IsDue.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never became true.");
            }

            await Task.Delay(50);
        }
    }
}
