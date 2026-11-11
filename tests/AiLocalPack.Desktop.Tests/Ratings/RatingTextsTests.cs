using AiLocalPack.Core.Ratings;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Ratings;

namespace AiLocalPack.Desktop.Tests.Ratings;

/// <summary>Why a like or report didn't go through, in plain words (task 63).</summary>
public sealed class RatingTextsTests
{
    [Theory]
    [InlineData(RatingProblem.Locked, "The registry's maintainers locked the pack's Discussion: it takes no new ratings.")]
    [InlineData(RatingProblem.NotFound, "The pack's Discussion is no longer on GitHub.")]
    [InlineData(RatingProblem.Network, "Can't reach GitHub. Check the connection and try again.")]
    [InlineData(RatingProblem.RateLimited, "GitHub's request limit is used up for now; try again in a while.")]
    [InlineData(RatingProblem.Forbidden, "GitHub refused it: nope")]
    [InlineData(RatingProblem.Unexpected, "Couldn't send it: nope")]
    public void Each_problem_has_its_words(RatingProblem problem, string expected)
    {
        // Act
        var text = RatingTexts.Error(new RatingError(problem, "nope"), Localizer.Current);

        // Assert
        text.Should().Be(expected);
    }
}
