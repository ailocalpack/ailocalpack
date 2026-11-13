using AiLocalPack.Core.Activity;
using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop.Tests.Requests;

/// <summary>The Activity panel's words for the player's translation requests (task 74).</summary>
public sealed class RequestActivityTextsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Taken_request_names_the_translator_and_the_game()
    {
        // Arrange
        var activity = ActivityEvent.Create(ActivityEventKind.RequestTaken, Now) with { Detail = "alice", GameName = "Neon Courier" };

        // Act
        var title = ActivityTexts.Title(activity, Localizer.Current);

        // Assert
        title.Should().Be("alice is translating Neon Courier for you");
    }

    [Fact]
    public void Done_request_names_the_pack_and_its_author()
    {
        // Arrange
        var activity = ActivityEvent.Create(ActivityEventKind.RequestDone, Now) with { PackTitle = "Неоновый курьер", Detail = "alice" };

        // Act
        var text = (ActivityTexts.Title(activity, Localizer.Current), ActivityTexts.Detail(activity, null, Localizer.Current));

        // Assert
        text.Should().Be(("Your request is done: «Неоновый курьер»", "Published by alice. Install it from the game page."));
    }
}
