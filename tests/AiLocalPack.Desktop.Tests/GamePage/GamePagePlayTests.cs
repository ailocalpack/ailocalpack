using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>The header's Play (task 25): launching, the starting / running states and launch problems.</summary>
public sealed class GamePagePlayTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 4, 0, TimeSpan.Zero);
    private static readonly GameId Manual = GameId.Fingerprint(new string('c', 64));

    private readonly FakeClock _clock = new(Now);
    private readonly FakeInstalledPacks _installed = new();
    private readonly ServiceProvider _services;

    public GamePagePlayTests()
    {
        _installed.Packs[(GameId.Steam(10), "ru")] = new InstalledPackState("lanterns", InstalledPackProblem.None, 4);
        _installed.Packs[(Manual, "ru")] = new InstalledPackState("lanterns", InstalledPackProblem.None, 4);
        _services = TestServices.Build(
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IInstalledPacks>(_installed);
                services.AddSingleton(sp => new GameSessions(
                    sp.GetRequiredService<GameLibrary>().Catalog,
                    sp.GetRequiredService<IProcessList>(),
                    sp.GetRequiredService<IProcessStarter>(),
                    sp.GetRequiredService<OsEnvironment>(),
                    _clock,
                    NullLogger<GameSessions>.Instance) { PollInterval = TimeSpan.FromMilliseconds(10) });
            },
            steam: s => s.AddGame(10, "Alpha"));
        _services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
    }

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _services.Dispose();

    private FakeProcesses Processes => _services.GetRequiredService<FakeProcesses>();

    private GameCatalog Catalog => _services.GetRequiredService<GameLibrary>().Catalog;

    private GamePageViewModel Open(GameId id) =>
        _services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Catalog.Find(id)!);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Installed_game_offers_play()
    {
        // Act
        var page = Open(GameId.Steam(10));

        // Assert
        (page.PrimaryText, page.CanRunPrimary).Should().Be(("Play", true));
    }

    [Fact]
    public async Task Play_launches_a_steam_game_through_steam()
    {
        // Arrange
        var page = Open(GameId.Steam(10));

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Until(() => Processes.OpenedUrls.Count > 0);
        Processes.OpenedUrls.Should().Equal(new Uri("steam://rungameid/10"));
    }

    [Fact]
    public async Task Launched_game_reads_starting_until_it_shows_up()
    {
        // Arrange
        var page = Open(GameId.Steam(10));

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Until(() => Processes.OpenedUrls.Count > 0);
        (page.PrimaryText, page.CanRunPrimary).Should().Be(("Starting…", false));
    }

    [Fact]
    public async Task Running_game_reads_running_since_its_start()
    {
        // Arrange
        var page = Open(GameId.Steam(10));
        page.PrimaryCommand.Execute(null);
        await Until(() => Processes.OpenedUrls.Count > 0);

        // Act
        Processes.Run(Path.Combine(PathNames.RealPath(Catalog.Find(GameId.Steam(10))!.InstallPath), "Alpha.exe"));

        // Assert
        await Until(() => page.PrimaryText == "Running");
        (page.PrimaryText, page.PrimaryCaption).Should().Be(("Running", "running since " + Now.ToString("t", Localizer.Current.Culture)));
    }

    [Fact]
    public async Task Ended_game_offers_play_again()
    {
        // Arrange
        var page = Open(GameId.Steam(10));
        page.PrimaryCommand.Execute(null);
        await Until(() => Processes.OpenedUrls.Count > 0);
        Processes.Run(Path.Combine(PathNames.RealPath(Catalog.Find(GameId.Steam(10))!.InstallPath), "Alpha.exe"));
        await Until(() => page.PrimaryText == "Running");

        // Act
        Processes.ExitAll();
        _clock.Advance(TimeSpan.FromSeconds(11));

        // Assert
        await Until(() => page.PrimaryText == "Play");
        (page.PrimaryText, page.CanRunPrimary).Should().Be(("Play", true));
    }

    [Fact]
    public async Task Refused_start_shows_the_reason()
    {
        // Arrange
        var page = Open(GameId.Steam(10));
        Processes.Refuse = new System.ComponentModel.Win32Exception("No application is associated with steam://");

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Until(() => page.HasLaunchError);
        (page.LaunchError, page.PrimaryText).Should().Be(("Couldn't start the game: No application is associated with steam://", "Play"));
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "A Windows build runs on Windows")]
    public async Task Windows_build_elsewhere_explains_why_it_does_not_start()
    {
        // Arrange
        var folder = _services.GetRequiredService<TempDir>().File("games/Lantern/Lantern.exe", "MZ").Combine("games/Lantern");
        Catalog.AddManual(new Game(Manual, "Lantern", GameSource.Manual, folder, null, null, "unknown", "none", null, true, "Lantern.exe"));
        var page = Open(Manual);

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Until(() => page.HasLaunchError);
        page.LaunchError.Should().Be(OperatingSystem.IsLinux()
            ? "This is a Windows build. To play it on Linux, add it to Steam as a non-Steam game and turn on Proton."
            : "This is a Windows build; it doesn't run on this system.");
    }

    [Fact]
    public async Task Pressing_play_again_clears_the_last_problem()
    {
        // Arrange
        var page = Open(GameId.Steam(10));
        Processes.Refuse = new System.ComponentModel.Win32Exception("refused");
        page.PrimaryCommand.Execute(null);
        await Until(() => page.HasLaunchError);

        // Act
        page.PrimaryCommand.Execute(null);

        // Assert
        await Until(() => Processes.OpenedUrls.Count > 0);
        page.HasLaunchError.Should().BeFalse();
    }
}
