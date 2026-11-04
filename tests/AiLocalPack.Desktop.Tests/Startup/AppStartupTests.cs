using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Startup;

public sealed class AppStartupTests
{
    private sealed class FakeTheming : IAppTheming
    {
        public AppTheme? Applied { get; private set; }

        public void Apply(AppTheme theme) => Applied = theme;
    }

    /// <summary>
    /// The app graph with saved settings in English (the applier sets the shared UI language from them, so tests stay
    /// English on any machine) and a fake theming.
    /// </summary>
    private static ServiceProvider Build(Action<SteamFixture>? steam = null, AppTheme theme = AppTheme.System)
    {
        var services = TestServices.Build(s => s.AddSingleton<IAppTheming, FakeTheming>(), steam);
        services.GetRequiredService<SettingsStore>().Update(s => s with { UiLanguage = "en", Theme = theme });
        return services;
    }

    private static StartupStep Step(ServiceProvider services, string id) =>
        services.GetRequiredService<AppStartup>().Progress.Find(id)!;

    private static async Task WaitUntilFinished(StartupStep step)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!step.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Settings_step_names_the_language_and_theme()
    {
        // Arrange
        using var services = Build(theme: AppTheme.Dark);

        // Act
        await services.GetRequiredService<AppStartup>().RunBlockingAsync();

        // Assert
        var step = Step(services, AppStartup.SettingsStep);
        (step.IsOk, step.Detail).Should().Be((true, "English · dark theme"));
    }

    [Fact]
    public async Task Settings_step_applies_the_loaded_settings()
    {
        // Arrange
        using var services = Build(theme: AppTheme.Light);

        // Act
        await services.GetRequiredService<AppStartup>().RunBlockingAsync();

        // Assert
        ((FakeTheming)services.GetRequiredService<IAppTheming>()).Applied.Should().Be(AppTheme.Light);
    }

    [Fact]
    public async Task Catalog_step_is_skipped_on_first_launch()
    {
        // Arrange
        using var services = Build();

        // Act
        await services.GetRequiredService<AppStartup>().RunBlockingAsync();

        // Assert
        var step = Step(services, AppStartup.CatalogStep);
        (step.IsSkipped, step.Detail).Should().Be((true, "No games yet: first launch"));
    }

    [Fact]
    public async Task Catalog_step_counts_the_saved_games()
    {
        // Arrange
        using var services = Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"));

        // Act
        await services.GetRequiredService<AppStartup>().RunBlockingAsync();

        // Assert
        var step = Step(services, AppStartup.CatalogStep);
        (step.IsOk, step.Detail).Should().Be((true, "2 games"));
    }

    [Fact]
    public async Task Catalog_step_warns_about_a_damaged_file()
    {
        // Arrange
        using var services = Build();
        var file = services.GetRequiredService<AppPaths>().GamesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{ not json", TestContext.Current.CancellationToken);

        // Act
        await services.GetRequiredService<AppStartup>().RunBlockingAsync();

        // Assert
        var step = Step(services, AppStartup.CatalogStep);
        (step.IsWarn, step.Detail).Should().Be((true, "The game list was damaged: backup saved, rebuilding from Steam"));
    }

    [Fact]
    public async Task Blocking_phase_leaves_the_background_checks_waiting()
    {
        // Arrange
        using var services = Build();
        var startup = services.GetRequiredService<AppStartup>();

        // Act
        await startup.RunBlockingAsync();

        // Assert
        startup.Progress.IsBlockingDone.Should().BeTrue();
        startup.Progress.Background.Should().OnlyContain(s => s.IsWaiting);
    }

    [Fact]
    public async Task Steam_step_names_the_new_games()
    {
        // Arrange
        SteamFixture? steam = null;
        using var services = Build(steam: s => steam = s.AddGame(10, "Alpha").AddGame(20, "Beta"));
        steam!.AddGame(30, "Gamma");
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        await WaitUntilFinished(Step(services, AppStartup.DetectionStep));

        // Assert
        Step(services, AppStartup.SteamStep).Detail.Should().Be("3 games · new: Gamma");
    }

    [Fact]
    public async Task Steam_step_only_counts_games_on_first_launch()
    {
        // Arrange
        using var services = Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"));
        File.Delete(services.GetRequiredService<AppPaths>().GamesFile);
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        await WaitUntilFinished(Step(services, AppStartup.DetectionStep));

        // Assert
        Step(services, AppStartup.SteamStep).Detail.Should().Be("2 games");
    }

    [Fact]
    public async Task Detection_step_counts_the_checked_games()
    {
        // Arrange
        SteamFixture? steam = null;
        using var services = Build(steam: s => steam = s.AddGame(10, "Alpha"));
        steam!.AddGame(30, "Gamma").AddGame(40, "Delta");
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        await WaitUntilFinished(Step(services, AppStartup.DetectionStep));

        // Assert
        var step = Step(services, AppStartup.DetectionStep);
        (step.IsOk, step.Detail).Should().Be((true, "Checked 2 games"));
    }

    [Fact]
    public async Task Detection_step_is_skipped_without_new_games()
    {
        // Arrange
        using var services = Build(steam: s => s.AddGame(10, "Alpha"));
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        await WaitUntilFinished(Step(services, AppStartup.DetectionStep));

        // Assert
        var step = Step(services, AppStartup.DetectionStep);
        (step.IsSkipped, step.Detail).Should().Be((true, "No new or updated games"));
    }

    [Fact]
    public async Task Steam_step_is_skipped_when_steam_is_missing()
    {
        // Arrange
        using var services = Build();
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        await WaitUntilFinished(Step(services, AppStartup.DetectionStep));

        // Assert
        var step = Step(services, AppStartup.SteamStep);
        (step.IsSkipped, step.Detail).Should().Be((true, "Steam not found"));
    }
}
