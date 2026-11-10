using System.Globalization;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>The publish flow's words in the UI language (the publish client's own messages are English, for the log and the CLI).</summary>
public static class PublishTexts
{
    /// <summary>The mini-step's label: "Fork the registry", "Commit the pack", "Open a pull request".</summary>
    public static string Step(PublishStep step, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[step switch
        {
            PublishStep.Fork => nameof(Strings.Publish_Step_Fork),
            PublishStep.Commit => nameof(Strings.Publish_Step_Commit),
            _ => nameof(Strings.Publish_Step_PullRequest),
        }];
    }

    /// <summary>Why the publish stopped, in plain words, with what to do.</summary>
    /// <param name="registry">The registry repository (named when it can't be found).</param>
    public static string Failure(PublishResult.Failed failed, RegistryRepo registry, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(localizer);
        var doing = localizer[failed.Step switch
        {
            PublishStep.Fork => nameof(Strings.Publish_Doing_Fork),
            PublishStep.Commit => nameof(Strings.Publish_Doing_Commit),
            PublishStep.PullRequest => nameof(Strings.Publish_Doing_PullRequest),
            _ => nameof(Strings.Publish_Doing_Account),
        }];
        return failed.Problem switch
        {
            PublishProblem.NotSignedIn => localizer[nameof(Strings.Publish_Error_NotSignedIn)],
            PublishProblem.Keychain => localizer[nameof(Strings.Publish_Error_Keychain)],
            PublishProblem.Unauthorized => localizer[nameof(Strings.Publish_Error_Unauthorized)],
            PublishProblem.Forbidden => localizer.Format(nameof(Strings.Publish_Error_Forbidden), doing),
            PublishProblem.RateLimited when failed.RetryAt is { } at =>
                localizer.Format(nameof(Strings.Publish_Error_RateLimitedUntil), at.ToLocalTime().ToString("t", localizer.Culture)),
            PublishProblem.RateLimited => localizer[nameof(Strings.Publish_Error_RateLimited)],
            PublishProblem.NotFound when failed.Step == PublishStep.Fork => localizer.Format(nameof(Strings.Publish_Error_NoRegistry), registry.FullName),
            PublishProblem.NotFound => localizer.Format(nameof(Strings.Publish_Error_NotFound), doing),
            PublishProblem.Rejected => localizer.Format(nameof(Strings.Publish_Error_Rejected), doing),
            PublishProblem.Conflict => localizer.Format(nameof(Strings.Publish_Error_Conflict), doing),
            PublishProblem.Network => localizer.Format(nameof(Strings.Publish_Error_Network), doing),
            PublishProblem.Invalid => localizer.Format(
                nameof(Strings.Publish_Error_Invalid),
                failed.Problems is { Count: > 0 } problems ? problems[0].ToString() : failed.Message),
            PublishProblem.OptedOut => localizer[nameof(Strings.Publish_Error_OptedOut)],
            PublishProblem.TargetGone => localizer[nameof(Strings.Publish_Error_TargetGone)],
            PublishProblem.ForkNotReady => localizer[nameof(Strings.Publish_Error_ForkNotReady)],
            _ => localizer.Format(nameof(Strings.Publish_Error_Unexpected), doing),
        };
    }

    /// <summary>"rev 4 of «Неон»" for a new revision, "«Курьер 2087»" for a new pack.</summary>
    public static string Target(string title, int? revision, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return revision is { } r
            ? localizer.Format(nameof(Strings.Publish_Target_Revision), r.ToString(CultureInfo.InvariantCulture), title)
            : localizer.Format(nameof(Strings.Publish_Target_Pack), title);
    }

    /// <summary>The failures that a new sign-in fixes: the flow signs out and shows the sign-in dialog before the next try.</summary>
    public static bool NeedsSignIn(PublishProblem problem) =>
        problem is PublishProblem.NotSignedIn or PublishProblem.Unauthorized or PublishProblem.Forbidden;
}
