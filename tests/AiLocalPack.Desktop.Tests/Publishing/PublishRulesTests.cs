using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Publishing;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>The publish dialogs' rules (task 61, design §7): when the first-time dialog shows, Continue, the preselected target.</summary>
public sealed class PublishRulesTests
{
    private static SimilarPack Similar(bool recommended) => new(
        new RegistryPack("neon", GameId.Steam(10), "ru", "Неон", new PackAuthor("alice"), PackStatus.Verified, 3, DateTimeOffset.UnixEpoch,
            new PackCoverage(9, 10), ["900"], 1, "games/steam-10/ru/neon/manifest.json", 100, new RegistryRating(42, 0, 0)),
        IsBase: false,
        RevisionRecommended: recommended);

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void The_first_time_dialog_shows_until_signed_in_and_the_license_accepted(bool signedIn, bool licensed, bool expected)
    {
        // Arrange
        // Act
        var needed = PublishRules.NeedsSignInDialog(signedIn, licensed);

        // Assert
        needed.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, PublishSignInMissing.SignIn)]
    [InlineData(false, true, PublishSignInMissing.SignIn)]
    [InlineData(true, false, PublishSignInMissing.License)]
    [InlineData(true, true, PublishSignInMissing.None)]
    public void The_hint_names_GitHub_first_then_the_license(bool signedIn, bool licensed, PublishSignInMissing expected)
    {
        // Arrange
        // Act
        var missing = PublishRules.Missing(signedIn, licensed);

        // Assert
        missing.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Continue_needs_GitHub_and_the_ticked_box(bool signedIn, bool licensed, bool expected)
    {
        // Arrange
        // Act
        var can = PublishRules.CanContinue(signedIn, licensed);

        // Assert
        can.Should().Be(expected);
    }

    [Fact]
    public void A_recommended_revision_is_preselected()
    {
        // Arrange
        var similar = Similar(recommended: true);

        // Act
        var target = PublishRules.DefaultTarget(similar);

        // Assert
        target.Should().Be(new PublishTarget.RevisionOf("neon"));
    }

    [Fact]
    public void Without_a_recommendation_a_separate_pack_is_preselected()
    {
        // Arrange
        var similar = Similar(recommended: false);

        // Act
        var target = PublishRules.DefaultTarget(similar);

        // Assert
        target.Should().BeOfType<PublishTarget.NewPack>();
    }
}
