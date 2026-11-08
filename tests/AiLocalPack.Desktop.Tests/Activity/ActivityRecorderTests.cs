using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.Install;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Activity;

/// <summary>What goes into the Activity store (task 38): installs as tasks and events, re-check findings as decisions.</summary>
public sealed class ActivityRecorderTests
{
    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    private static ActivityStore Store(InstallBed bed) => bed.Services.GetRequiredService<ActivityRecorder>().Store;

    private static InstallFlow Flow(InstallBed bed) => bed.Services.GetRequiredService<InstallFlow>();

    private static InstalledPackMonitor Monitor(InstallBed bed) => bed.Services.GetRequiredService<InstalledPackMonitor>();

    /// <summary>Installs "lanterns" through the flow and waits for it; returns the original bytes of the translated file.</summary>
    private static async Task<byte[]> InstallAsync(InstallBed bed)
    {
        var original = File.ReadAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => !Flow(bed).IsBusy && Store(bed).Feed.Count > 0);
        return original;
    }

    private static void PutBack(InstallBed bed, byte[] original) =>
        File.WriteAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile), original);

    private static void RunGame(InstallBed bed) =>
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));

    [Fact]
    public async Task Install_adds_an_unread_feed_event()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        _ = Store(bed);

        // Act
        await InstallAsync(bed);

        // Assert
        Store(bed).Feed.Single().Should().BeEquivalentTo(new
        {
            Kind = ActivityEventKind.PackInstalled,
            Game = InstallBed.Alpha,
            GameName = "Alpha",
            PackTitle = "lanterns",
            Revision = 1,
            IsRead = false,
            IsDecision = false,
        });
    }

    [Fact]
    public async Task Install_runs_as_a_task_until_it_ends()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var store = Store(bed);
        var seen = new List<ActivityTask>();
        store.Changed += (_, _) =>
        {
            lock (seen)
            {
                seen.AddRange(store.Tasks);
            }
        };

        // Act
        await InstallAsync(bed);

        // Assert
        (seen.Select(t => (t.Kind, t.GameName)).Distinct().Single(), store.Tasks.Count, seen.Max(t => t.Fraction))
            .Should().Be(((ActivityTaskKind.Install, "Alpha"), 0, 1.0));
    }

    [Fact]
    public async Task Failed_install_goes_to_the_feed_with_its_reason()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        _ = Store(bed);
        RunGame(bed);

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Store(bed).Feed.Count > 0);

        // Assert
        Store(bed).Feed.Single().Should().BeEquivalentTo(new
        {
            Kind = ActivityEventKind.InstallFailed,
            Tone = ActivityTone.Bad,
            Detail = "The game is running. Close it and retry.",
            CanRetry = true,
            IsDecision = false,
        });
    }

    [Fact]
    public async Task Automatic_reapply_goes_to_the_feed_without_a_toast()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        var toasts = bed.Services.GetRequiredService<ToastService>();
        toasts.Items.Clear();

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (Store(bed).Feed.First().Kind, Store(bed).Decisions.Count, toasts.Items.Count)
            .Should().Be((ActivityEventKind.PackAutoReapplied, 0, 0));
    }

    [Fact]
    public async Task Failed_automatic_reapply_needs_a_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        RunGame(bed);

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Store(bed).Decisions.Single().Should().BeEquivalentTo(new
        {
            Kind = ActivityEventKind.AutoReapplyFailed,
            Tone = ActivityTone.Bad,
            PackTitle = "lanterns",
            CanRetry = true,
            FilesRestored = true,
        });
    }

    [Fact]
    public async Task Same_failure_found_again_is_not_added_twice()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        RunGame(bed);
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);
        Store(bed).DecideLater(Store(bed).Decisions.Single().Id);

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (Store(bed).Decisions.Count, Store(bed).Feed.Count(e => e.Kind == ActivityEventKind.AutoReapplyFailed)).Should().Be((0, 1));
    }

    [Fact]
    public async Task Incompatible_pack_needs_a_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAsync(bed);
        File.Delete(Path.Combine(bed.GameDir, InstallBed.SystemFile));

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Store(bed).Decisions.Single().Should().BeEquivalentTo(new { Kind = ActivityEventKind.PackIncompatible, Tone = ActivityTone.Attention, Revision = 1 });
    }

    [Fact]
    public async Task With_reapply_off_the_wiped_pack_needs_a_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { ReapplyAfterGameUpdates = false });

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Store(bed).Decisions.Single().Kind.Should().Be(ActivityEventKind.PackNotApplied);
    }

    [Fact]
    public async Task Successful_reapply_settles_the_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { ReapplyAfterGameUpdates = false });
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Act
        await Flow(bed).ReapplyAsync(bed.Game);

        // Assert
        (Store(bed).Decisions.Count, Store(bed).Feed.First().Kind).Should().Be((0, ActivityEventKind.PackReapplied));
    }

    [Fact]
    public async Task Intact_pack_settles_an_old_decision()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var original = await InstallAsync(bed);
        var installed = File.ReadAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        PutBack(bed, original);
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { ReapplyAfterGameUpdates = false });
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile), installed);

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Store(bed).Decisions.Should().BeEmpty();
    }
}
