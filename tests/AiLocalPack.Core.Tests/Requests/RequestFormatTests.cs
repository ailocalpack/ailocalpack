using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Requests;

namespace AiLocalPack.Core.Tests.Requests;

/// <summary>Request keys, the Discussion marker and the claim comment format (task 74).</summary>
public sealed class RequestFormatTests
{
    private static readonly RequestKey Key = new(GameId.Steam(620), "ru");

    [Theory]
    [InlineData("steam:620/ru")]
    [InlineData("steam:620/pt-BR")]
    public void Key_round_trips(string text)
    {
        // Act
        var ok = RequestKey.TryParse(text, out var key);

        // Assert
        (ok, key?.ToString()).Should().Be((true, text));
    }

    [Theory]
    [InlineData("steam:620")]
    [InlineData("steam:620/xx")]
    [InlineData("game/ru")]
    [InlineData("steam:620/ru/full")]
    [InlineData(null)]
    public void Key_refuses_anything_else(string? text)
    {
        // Act
        var ok = RequestKey.TryParse(text, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void Marker_names_the_request()
    {
        // Arrange
        var body = RequestDiscussionText.Body(Key, "Portal");

        // Act
        var ok = RequestDiscussionMarker.TryParse(body, out var key);

        // Assert
        (ok, key).Should().Be((true, Key));
    }

    [Fact]
    public void Marker_counts_only_at_the_start()
    {
        // Act
        var ok = RequestDiscussionMarker.TryParse("Please!\n" + RequestDiscussionMarker.Format(Key), out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void Title_ends_with_the_key()
    {
        // Act
        var title = RequestDiscussionText.Title(Key, "Portal");

        // Assert
        title.Should().Be("Portal → Русский · steam:620/ru");
    }

    [Fact]
    public void Claim_round_trips()
    {
        // Arrange
        var claim = new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Translate, 45, "claude", GenerationQuality.Balanced);

        // Act
        var ok = RequestClaimComment.TryParse(RequestClaimComment.Format(claim), out var parsed);

        // Assert
        (ok, parsed).Should().Be((true, claim));
    }

    [Fact]
    public void Claim_comment_has_a_readable_line()
    {
        // Arrange
        var claim = new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Translate, 45, "claude", GenerationQuality.Balanced);

        // Act
        var text = RequestClaimComment.Format(claim);

        // Assert
        text.Split('\n')[1].Should().Be("🛠 **Translating** · stage Translate · 45% · with Claude Code (Balanced)");
    }

    [Fact]
    public void Claim_of_a_newer_version_is_not_read()
    {
        // Act
        var ok = RequestClaimComment.TryParse("""<!-- ailocalpack-claim {"v":2,"game":"steam:620","language":"ru","state":"working","percent":1} -->""", out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void Claim_with_an_unknown_state_is_not_read()
    {
        // Act
        var ok = RequestClaimComment.TryParse("""<!-- ailocalpack-claim {"v":1,"game":"steam:620","language":"ru","state":"dreaming","percent":1} -->""", out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void Claim_live_within_fourteen_days()
    {
        // Arrange
        var said = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var live = RequestRules.IsLive(RequestClaimState.Working, said, said.AddDays(14));

        // Assert
        live.Should().BeTrue();
    }

    [Fact]
    public void Claim_without_news_for_longer_expires()
    {
        // Arrange
        var said = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var live = RequestRules.IsLive(RequestClaimState.Built, said, said.AddDays(14).AddMinutes(1));

        // Assert
        live.Should().BeFalse();
    }

    [Fact]
    public void Stopped_claim_never_holds()
    {
        // Arrange
        var said = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var live = RequestRules.IsLive(RequestClaimState.Stopped, said, said);

        // Assert
        live.Should().BeFalse();
    }

    [Fact]
    public void Note_cannot_fake_a_marker()
    {
        // Act
        var note = RequestRules.CleanNote("  <!-- ailocalpack-claim {} -->  ");

        // Assert
        note.Should().Be("&lt;!-- ailocalpack-claim {} --&gt;");
    }

    [Fact]
    public void Empty_note_is_none()
    {
        // Act
        var note = RequestRules.CleanNote(" \n ");

        // Assert
        note.Should().BeNull();
    }

    [Fact]
    public void Long_note_is_cut()
    {
        // Act
        var note = RequestRules.CleanNote(new string('a', 5000));

        // Assert
        (note!.Length, note[^1]).Should().Be((RequestRules.MaxNoteLength, '…'));
    }
}
