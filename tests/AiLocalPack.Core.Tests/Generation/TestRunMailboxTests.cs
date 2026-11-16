using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>How the toolkit server asks the app for a test run during a fix, and gets the answer.</summary>
public sealed class TestRunMailboxTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private TestRunMailbox Mailbox => new(_dir.Path);

    [Fact]
    public void An_ask_without_an_answer_is_pending()
    {
        // Arrange
        Mailbox.Ask(new TestRunAsk(1, 60));

        // Act
        var pending = Mailbox.Pending();

        // Assert
        pending.Should().Equal(new TestRunAsk(1, 60));
    }

    [Fact]
    public void An_answered_ask_is_no_longer_pending()
    {
        // Arrange
        Mailbox.Ask(new TestRunAsk(1, null));
        Mailbox.Reply(1, new TestRunAnswer("passed", "ok", 4));

        // Act
        var pending = Mailbox.Pending();

        // Assert
        pending.Should().BeEmpty();
    }

    [Fact]
    public void The_answer_comes_back_as_written()
    {
        // Arrange
        var answer = new TestRunAnswer("crashed", "closed", 3, RanSeconds: 12.5, ExitCode: "0xC0000409", Screenshots: new TestRunPictures(null, "/a.png", "/b.png"), GameLog: ["x", "y"]);
        Mailbox.Reply(2, answer);

        // Act
        var read = Mailbox.Answer(2);

        // Assert
        read.Should().BeEquivalentTo(answer);
    }

    [Fact]
    public void The_next_number_follows_the_highest_ask()
    {
        // Arrange
        Mailbox.Ask(new TestRunAsk(1, null));
        Mailbox.Ask(new TestRunAsk(2, null));

        // Act
        var next = Mailbox.NextNumber();

        // Assert
        next.Should().Be(3);
    }

    [Fact]
    public void Clearing_removes_every_ask_and_answer()
    {
        // Arrange
        Mailbox.Ask(new TestRunAsk(1, null));
        Mailbox.Reply(1, new TestRunAnswer("passed", "ok", 4));

        // Act
        Mailbox.Clear();

        // Assert
        Directory.Exists(Mailbox.Folder).Should().BeFalse();
    }
}
