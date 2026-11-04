using System.Net;
using AiLocalPack.Core.Registry;
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
    private static ServiceProvider Build(
        Action<SteamFixture>? steam = null, AppTheme theme = AppTheme.System, string? registry = null, Action<IServiceCollection>? configure = null)
    {
        var services = TestServices.Build(
            s =>
            {
                s.AddSingleton<IAppTheming, FakeTheming>();
                configure?.Invoke(s);
            },
            steam,
            registry);
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

    private const string IndexUrl = "https://registry.example/index.json";

    private const string OnePackIndex = """
        { "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": { "steam:620": { "packs": { "ru": [ {
          "id": "full", "title": "Full", "author": { "github": "alice" }, "status": "alpha", "revision": 1,
          "updated": "2026-11-01T00:00:00Z", "coverage": { "translated": 1, "total": 2 }, "buildIds": ["1"], "buildCount": 1,
          "manifest": "games/steam-620/ru/full/manifest.json", "size": 10 } ] } } } }
        """;

    private static async Task<StartupStep> RunIndexStep(ServiceProvider services)
    {
        var startup = services.GetRequiredService<AppStartup>();
        await startup.RunBlockingAsync();
        startup.StartBackground();
        var step = Step(services, AppStartup.IndexStep);
        await WaitUntilFinished(step);
        return step;
    }

    [Fact]
    public async Task Index_step_counts_the_packs()
    {
        // Arrange
        using var registry = new TempDir().File("index.json", OnePackIndex);
        using var services = Build(registry: registry.Path);

        // Act
        var step = await RunIndexStep(services);

        // Assert
        (step.IsOk, step.Detail).Should().Be((true, "1 pack"));
    }

    [Fact]
    public async Task Index_step_warns_when_the_registry_is_unreachable()
    {
        // Arrange
        using var services = Build();

        // Act
        var step = await RunIndexStep(services);

        // Assert
        (step.IsWarn, step.Detail).Should().Be((true, "Can't reach the pack registry"));
    }

    [Fact]
    public async Task Index_step_shows_the_saved_list_when_offline()
    {
        // Arrange
        using var http = new FakeHttpHandler();
        http.OnGet(IndexUrl, OnePackIndex);
        using var client = http.CreateClient();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero));
        using var services = Build(registry: IndexUrl, configure: s => s.AddSingleton(sp => new RegistryIndexClient(
            client, sp.GetRequiredService<AppPaths>(), clock, options: new RegistryIndexClientOptions(TimeSpan.FromSeconds(5), Attempts: 1))));
        RegistryLocation.TryParse(IndexUrl, out var location, out _);
        await services.GetRequiredService<RegistryIndexClient>().FetchAsync(location!, TestContext.Current.CancellationToken);
        http.OnGet(IndexUrl, HttpStatusCode.BadGateway);

        // Act
        var step = await RunIndexStep(services);

        // Assert
        var savedAt = clock.GetUtcNow().ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en"));
        (step.IsWarn, step.Detail).Should().Be((true, $"Offline: showing the saved list from {savedAt}"));
    }

    [Fact]
    public async Task Index_step_warns_about_an_invalid_address()
    {
        // Arrange
        using var services = Build(registry: "http://registry.example");

        // Act
        var step = await RunIndexStep(services);

        // Assert
        (step.IsWarn, step.Detail).Should().Be((true, "The pack registry address is not valid: details are in the log"));
    }
}
