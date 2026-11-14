using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>A long Fix with AI thread folds its earlier rounds and long step lists (design §6, "one conversation").</summary>
public sealed class FixThreadFoldTests
{
    private static FixSystemItem System(string text) => new(text, false);

    private static FixPlayerItem Player(string text) => new(text, []);

    private static FixAgentItem Agent(int steps = 1, bool working = false) =>
        new("Claude Code", [.. Enumerable.Range(1, steps).Select(i => new FixStepRow($"step {i}", false))], "Done.", [], null, null, string.Empty, string.Empty, working);

    [Fact]
    public void Two_rounds_stay_open()
    {
        // Arrange
        FixItem[] items = [System("Draft 1 built"), Player("a"), Agent(), System("You played Draft 2"), Player("b"), Agent()];

        // Act
        var start = FixViewModel.FoldStart(items);

        // Assert
        start.Should().Be(0);
    }

    [Fact]
    public void A_third_round_folds_the_first_with_its_lines()
    {
        // Arrange
        FixItem[] items =
        [
            System("Draft 1 built"), Player("a"), Agent(),
            System("You played Draft 2"), Player("b"), Agent(),
            System("You played Draft 3"), Player("c"), Agent(),
        ];

        // Act
        var start = FixViewModel.FoldStart(items);

        // Assert
        start.Should().Be(3);
    }

    [Fact]
    public void Little_to_fold_stays_open()
    {
        // Arrange
        FixItem[] items = [Player("a"), Agent(), Player("b"), Agent(), Player("c"), Agent()];

        // Act
        var start = FixViewModel.FoldStart(items);

        // Assert
        start.Should().Be(0);
    }

    [Fact]
    public void Many_steps_fold_after_the_first_four()
    {
        // Arrange
        var agent = Agent(steps: 7);

        // Act
        var folded = (agent.FoldsSteps, agent.HeadSteps.Count, agent.MoreSteps.Count);

        // Assert
        folded.Should().Be((true, 4, 3));
    }

    [Fact]
    public void Five_steps_stay_open()
    {
        // Arrange
        var agent = Agent(steps: 5);

        // Act
        var folded = (agent.FoldsSteps, agent.HeadSteps.Count, agent.MoreSteps.Count);

        // Assert
        folded.Should().Be((false, 5, 0));
    }

    [Fact]
    public void A_working_agent_shows_every_step()
    {
        // Arrange
        var agent = Agent(steps: 9, working: true);

        // Act
        var folds = agent.FoldsSteps;

        // Assert
        folds.Should().BeFalse();
    }
}
