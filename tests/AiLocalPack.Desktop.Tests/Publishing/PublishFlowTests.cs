using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Tests.GitHub;
using AiLocalPack.Desktop.Tests.Install;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// The one-click publish (task 61, design §7) over a real local pack: which dialog shows when, what is sent, the Activity
/// running item and outcome, the toasts, the pack's state afterwards.
/// </summary>
public sealed class PublishFlowTests
{
    private static readonly DateTimeOffset Tried = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

    private static void SignedInAndLicensed(InstallBed bed)
    {
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { PublishLicenseAccepted = true });
    }

    private static PublishFlow Flow(InstallBed bed) => bed.Services.GetRequiredService<PublishFlow>();

    private static FakePackPublishing Publisher(InstallBed bed) => bed.Services.GetRequiredService<FakePackPublishing>();

    private static DialogViewModel? Dialog(InstallBed bed) => bed.Services.GetRequiredService<IDialogService>().Current;

    private static Core.Packs.LocalPack Fresh(InstallBed bed) =>
        bed.Services.GetRequiredService<LocalPacks>().Find(InstallBed.Alpha, "ru", "claude-code")!;

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Signed_out_the_first_time_dialog_shows_and_nothing_is_sent()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);

        // Act
        Flow(bed).Publish(bed.Game, pack);

        // Assert
        (Dialog(bed), Publisher(bed).Requests.Count).Should().Match<(DialogViewModel?, int)>(x => x.Item1 is PublishSignInDialogViewModel && x.Item2 == 0);
    }

    [Fact]
    public async Task Signed_in_without_the_license_the_first_time_dialog_shows()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;

        // Act
        Flow(bed).Publish(bed.Game, pack);

        // Assert
        Dialog(bed).Should().BeOfType<PublishSignInDialogViewModel>();
    }

    [Fact]
    public async Task A_pack_not_tried_in_game_is_not_published()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        SignedInAndLicensed(bed);

        // Act
        Flow(bed).Publish(bed.Game, pack);

        // Assert
        (Dialog(bed), Publisher(bed).Requests.Count).Should().Be((null, 0));
    }

    [Fact]
    public async Task Without_similar_packs_it_is_sent_at_once_as_a_new_pack()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);

        // Act
        Flow(bed).Publish(bed.Game, pack);
        await Until(() => !Flow(bed).IsBusy && Publisher(bed).Requests.Count > 0);

        // Assert
        (Dialog(bed), Publisher(bed).Requests.Single().Target, Publisher(bed).Requests.Single().GameName)
            .Should().Be((null, new PublishTarget.NewPack(), "Alpha"));
    }

    [Fact]
    public async Task A_similar_registry_pack_asks_with_the_recommended_revision_chosen()
    {
        // Arrange: the registry's pack translates fewer strings, so a new revision of it is recommended
        using var bed = await InstallBed.CreateAsync(b => b.Registry.Pack(10, "ru", "neon", translated: 5));
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);

        // Act
        Flow(bed).Publish(bed.Game, pack);

        // Assert
        var dialog = Dialog(bed).Should().BeOfType<SimilarPackDialogViewModel>().Subject;
        (dialog.IsRecommended, dialog.Target, dialog.RevisionTitle).Should().Be((true, new PublishTarget.RevisionOf("neon"), "New revision of «neon» (rev 2)"));
    }

    [Fact]
    public async Task The_similar_pack_dialog_sends_the_chosen_target()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.Registry.Pack(10, "ru", "neon", translated: 5));
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        Flow(bed).Publish(bed.Game, pack);
        var dialog = (SimilarPackDialogViewModel)Dialog(bed)!;
        dialog.ChooseSeparateCommand.Execute(null);

        // Act
        dialog.PublishCommand.Execute(null);
        await Until(() => !Flow(bed).IsBusy && Publisher(bed).Requests.Count > 0);

        // Assert
        (Dialog(bed), Publisher(bed).Requests.Single().Target).Should().Be((null, new PublishTarget.NewPack()));
    }

    [Fact]
    public async Task Continue_in_the_first_time_dialog_remembers_the_license_and_goes_on()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        bed.Services.GetRequiredService<FakeGitHubHost>().Account = FakeGitHubHost.Octocat;
        Flow(bed).Publish(bed.Game, pack);
        var dialog = (PublishSignInDialogViewModel)Dialog(bed)!;
        dialog.IsLicenseAccepted = true;

        // Act
        dialog.ContinueCommand.Execute(null);
        await Until(() => !Flow(bed).IsBusy && Publisher(bed).Requests.Count > 0);

        // Assert
        (bed.Services.GetRequiredService<SettingsStore>().Current.PublishLicenseAccepted, Publisher(bed).Requests.Count).Should().Be((true, 1));
    }

    [Fact]
    public async Task While_sending_the_pack_is_at_its_step_with_an_Activity_running_item()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        Publisher(bed).Steps = [PublishStep.Fork, PublishStep.Commit];
        Publisher(bed).Hold();

        // Act
        Flow(bed).Publish(bed.Game, pack);
        // The Activity item follows the strip's step a moment later: wait for the last of them.
        var activity = bed.Services.GetRequiredService<ActivityStore>();
        await Until(() => activity.Tasks.SingleOrDefault()?.Step == "Commit the translation");

        // Assert
        var task = activity.Tasks.Single();
        (Flow(bed).StatusOf(pack).Phase, Flow(bed).StatusOf(pack).Run?.Step, task.Kind, task.Title)
            .Should().Be((PublishPhase.Sending, (PublishStep?)PublishStep.Commit, ActivityTaskKind.Publish, "Publishing «Claude Code translation»…"));
        Publisher(bed).Release();
    }

    [Fact]
    public async Task Once_the_PR_is_open_CI_checks_the_pack_and_Activity_says_so()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);

        // Act
        var result = await Flow(bed).SendAsync(bed.Game, pack, new PublishTarget.NewPack());

        // Assert
        var store = bed.Services.GetRequiredService<ActivityStore>();
        var sent = store.Feed.Single();
        (result, Flow(bed).StatusOf(Fresh(bed)).Phase, sent.Kind, sent.Detail, store.Tasks.Count)
            .Should().Match<(PublishResult?, PublishPhase, ActivityEventKind, string?, int)>(x =>
                x.Item1 is PublishResult.Published && x.Item2 == PublishPhase.Checking && x.Item3 == ActivityEventKind.PackSubmitted
                && x.Item4 == "PR #7 opened · CI is checking the translation" && x.Item5 == 0);
    }

    [Fact]
    public async Task A_failure_keeps_the_pack_failed_with_a_toast_and_an_Activity_entry()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        Publisher(bed).Result = _ => new PublishResult.Failed(PublishStep.Fork, PublishProblem.Network, "No answer.");

        // Act
        await Flow(bed).SendAsync(bed.Game, pack, new PublishTarget.NewPack());

        // Assert
        var failed = bed.Services.GetRequiredService<ActivityStore>().Feed.Single();
        var toast = bed.Services.GetRequiredService<ToastService>().Items.Single();
        (Flow(bed).StatusOf(Fresh(bed)).Phase, failed.Kind, failed.Detail, toast.Tone)
            .Should().Be((PublishPhase.Failed, ActivityEventKind.PublishFailed,
                "GitHub couldn't be reached while forking the registry; check the connection and publish again: it continues where it stopped.", ToastTone.Bad));
    }

    [Fact]
    public async Task After_a_revoked_sign_in_publishing_again_signs_out_and_asks_GitHub_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack(tried: Tried);
        SignedInAndLicensed(bed);
        Publisher(bed).Result = _ => new PublishResult.Failed(null, PublishProblem.Unauthorized, "Bad credentials.");
        await Flow(bed).SendAsync(bed.Game, pack, new PublishTarget.NewPack());
        var github = bed.Services.GetRequiredService<FakeGitHubHost>();

        // Act
        Flow(bed).Publish(bed.Game, Fresh(bed));
        await Until(() => Dialog(bed) is not null);

        // Assert
        (Dialog(bed) is PublishSignInDialogViewModel, github.Account).Should().Be((true, null));
    }
}
