using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Toolkit.Folders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Ratings;

/// <summary>
/// Game 10 with the registry pack «full» (rev 1, by alice, its Discussion #42) installed, a fast session poll on a fake
/// clock, and the fake Discussion; <see cref="PlayedAsync"/> marks the pack played as a game session would.
/// </summary>
internal sealed class RatingBed : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    public static readonly Uri Discussion = new("https://github.com/ailocalpack/registry/discussions/42");

    private RatingBed(InstallBed install, FakeClock clock)
    {
        Install = install;
        Clock = clock;
    }

    public InstallBed Install { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => Install.Services;

    public FakePackDiscussions Discussions => Services.GetRequiredService<FakePackDiscussions>();

    public FakeGitHubHost GitHub => Services.GetRequiredService<FakeGitHubHost>();

    public PackRatings Ratings => Services.GetRequiredService<PackRatings>();

    public PackRatingStore Store => Services.GetRequiredService<PackRatingStore>();

    public DialogViewModel? Dialog => Services.GetRequiredService<IDialogService>().Current;

    public ToastService Toasts => Services.GetRequiredService<ToastService>();

    public RegistryPack Pack => Services.GetRequiredService<AiLocalPack.Desktop.Registry.PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru")[0];

    /// <param name="installed">The pack is installed (else it is only in the registry).</param>
    /// <param name="discussion">The index lists the pack's Discussion.</param>
    public static async Task<RatingBed> CreateAsync(bool installed = true, bool discussion = true, bool signedIn = true)
    {
        var clock = new FakeClock(Now);
        var install = await InstallBed.CreateAsync(
            bed => bed.RecipePack("full", discussion: discussion ? Discussion : null),
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(sp => new GameSessions(
                    sp.GetRequiredService<GameLibrary>().Catalog,
                    sp.GetRequiredService<IProcessList>(),
                    sp.GetRequiredService<IProcessStarter>(),
                    sp.GetRequiredService<OsEnvironment>(),
                    clock,
                    NullLogger<GameSessions>.Instance) { PollInterval = TimeSpan.FromMilliseconds(10) });
            });
        var bed = new RatingBed(install, clock);
        _ = bed.Ratings;
        if (signedIn)
        {
            bed.GitHub.Account = FakeGitHubHost.Octocat;
        }

        if (installed)
        {
            await bed.Services.GetRequiredService<InstallFlow>().InstallAsync(install.Game, bed.Pack);
            bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha).Should().NotBeNull("the pack installs");
        }

        return bed;
    }

    public GamePageViewModel Page(GamePageTab tab = GamePageTab.Packs)
    {
        var page = Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(Install.Game, tab);
        page.Packs.Activate();
        return page;
    }

    /// <summary>The installed pack's rating block in the Packs tab.</summary>
    public static PackRatingViewModel? Block(GamePageViewModel page) => page.Packs.Rows.First(r => r.Pack.Id == "full").Rating;

    /// <summary>A game session with the pack installed ended (as <see cref="PackRatings"/> records it).</summary>
    public void Played()
    {
        Clock.Advance(TimeSpan.FromHours(1));
        Store.Played(InstallBed.Alpha, "ru", "full", 1, Clock.GetUtcNow());
        Clock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>The game is launched from the page, runs and is closed again.</summary>
    public async Task PlayAndCloseAsync(GamePageViewModel page)
    {
        var processes = Services.GetRequiredService<FakeProcesses>();
        page.PrimaryCommand.Execute(null);
        await Until(() => processes.OpenedUrls.Count > 0);
        processes.Run(Path.Combine(PathNames.RealPath(Install.GameDir), "Game.exe"));
        var sessions = Services.GetRequiredService<GameSessions>();
        await Until(() => sessions.Find(InstallBed.Alpha)?.Phase == GameSessionPhase.Running);
        processes.ExitAll();
        Clock.Advance(TimeSpan.FromSeconds(11));
        await Until(() => sessions.Find(InstallBed.Alpha) is null);
    }

    public static Task Until(Func<bool> condition) => Wait.UntilAsync(condition);

    public void Dispose() => Install.Dispose();
}
