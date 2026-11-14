using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The job told as the start of the Create with AI conversation (design §6, "one conversation").</summary>
public sealed class GenerationStoryTests
{
    private static readonly Localizer English = new(CultureInfo.GetCultureInfo("en"));

    private static GenerationJob Job(GenerationStage stage, GenerationOptions? options = null, GenerationCounters? counters = null) => new()
    {
        Id = "job-1",
        Game = GameId.Steam(10),
        GameName = "Alpha",
        Language = "ru",
        Agent = "claude",
        Options = options ?? GenerationOptions.Default,
        Stage = stage,
        Counters = counters ?? GenerationCounters.Zero,
        Created = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };

    private static IReadOnlyList<string> Texts(IReadOnlyList<GenerationStoryItem> items) =>
        [.. items.Select(i => i switch
        {
            GenerationStoryRequest r => "> " + r.Text,
            GenerationStoryLine l => "- " + l.Text,
            _ => "?",
        })];

    [Fact]
    public void A_translating_job_tells_the_request_and_the_finished_stages()
    {
        // Arrange
        var job = Job(GenerationStage.Translate, new GenerationOptions(ReviewGlossary: true), new GenerationCounters(Strings: 1842));

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        Texts(story).Should().Equal(
            "> Create a Balanced translation into Русский with Claude Code. Let me review the glossary first.",
            "- Setup · working copy ready",
            "- Extract · strings: 1,842",
            "- Glossary · reviewed by you");
    }

    [Fact]
    public void A_job_in_setup_tells_only_the_request()
    {
        // Arrange
        var job = Job(GenerationStage.Setup);

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        Texts(story).Should().Equal("> Create a Balanced translation into Русский with Claude Code.");
    }

    [Fact]
    public void A_thorough_job_tells_its_study_stage()
    {
        // Arrange
        var job = Job(GenerationStage.Glossary, new GenerationOptions(Quality: GenerationQuality.Thorough));

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        Texts(story).Should().Contain("- Study · game brief written");
    }

    [Fact]
    public void An_update_job_asks_for_the_new_strings()
    {
        // Arrange
        var job = Job(GenerationStage.Extract, new GenerationOptions(Base: new GenerationBase("claude-code", 3, false, "Claude Code translation")));

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        Texts(story)[0].Should().Be("> Translate the new strings into Русский with Claude Code.");
    }

    [Fact]
    public void A_finished_job_without_its_pack_tells_every_stage_but_the_build()
    {
        // Arrange
        var job = Job(GenerationStage.Done, counters: new GenerationCounters(Strings: 64, Chunks: 3));

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        Texts(story).Should().Equal(
            "> Create a Balanced translation into Русский with Claude Code.",
            "- Setup · working copy ready",
            "- Extract · strings: 64",
            "- Glossary · ready",
            "- Translate · chunks: 3");
    }

    [Fact]
    public void No_job_has_no_story()
    {
        // Arrange
        GenerationJob? job = null;

        // Act
        var story = GenerationStory.Of(job, null, English);

        // Assert
        story.Should().BeEmpty();
    }
}
