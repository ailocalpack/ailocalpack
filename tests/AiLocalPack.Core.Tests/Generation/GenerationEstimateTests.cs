using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Generation;

public sealed class GenerationEstimateTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    [Theory]
    [InlineData("Open the door.", 3)]
    [InlineData("Don't stop — it's well-known!", 4)]
    [InlineData("Harold has 100 gold.", 4)]
    [InlineData("勇者の剣", 4)]
    [InlineData("Привет, мир", 2)]
    [InlineData("  ", 0)]
    public void Words_are_runs_of_letters_and_each_CJK_character(string text, long words)
    {
        // Arrange
        // (text and expected count from the data)

        // Act
        var count = TextSize.CountWords(text);

        // Assert
        count.Should().Be(words);
    }

    [Fact]
    public void Text_size_counts_strings_words_and_characters()
    {
        // Arrange
        string[] originals = ["New Game", "Continue", "Options"];

        // Act
        var size = TextSize.Of(originals);

        // Assert
        size.Should().Be(new TextSize(3, 4, 23));
    }

    [Fact]
    public void Strings_are_cut_into_chunks()
    {
        // Arrange
        var text = new TextSize(GenerationEstimateModel.StringsPerChunk * 2 + 1, 1000, 6000);

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        estimate.Chunks.Should().Be(3);
    }

    [Fact]
    public void Tokens_follow_the_model()
    {
        // Arrange
        var text = new TextSize(100, 2000, 12_000);

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        estimate.Should().BeEquivalentTo(new
        {
            InputTokens = 3000L + (100 * 40) + (2 * 12_000) + 150_000,
            OutputTokens = (long)Math.Round((3000 * 1.6) + (100 * 25) + (2 * 500) + 15_000),
        });
    }

    [Fact]
    public void Time_and_limits_have_a_low_and_a_high_bound()
    {
        // Arrange
        var text = new TextSize(5000, 50_000, 300_000);

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        new { TimeOrdered = estimate.TimeLow < estimate.TimeHigh, LimitsOrdered = estimate.LimitWindowsLow < estimate.LimitWindowsHigh }
            .Should().BeEquivalentTo(new { TimeOrdered = true, LimitsOrdered = true });
    }

    [Fact]
    public void Limit_days_are_windows_over_a_players_day()
    {
        // Arrange
        var text = new TextSize(5000, 50_000, 300_000);

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        estimate.LimitDaysHigh.Should().BeApproximately(estimate.LimitWindowsHigh / GenerationEstimateModel.LimitWindowsPerDay, 1e-9);
    }

    [Fact]
    public void An_unknown_format_costs_more_setup_than_a_toolkit_one()
    {
        // Arrange
        var text = new TextSize(1000, 10_000, 60_000);
        var known = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Act
        var unknown = GenerationEstimateModel.Estimate(text, EstimateBasis.FileSizes);

        // Assert
        unknown.TimeHigh.Should().BeGreaterThan(known.TimeHigh);
    }

    [Fact]
    public void A_game_without_strings_costs_only_the_setup()
    {
        // Arrange
        var text = TextSize.Empty;

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        estimate.Should().BeEquivalentTo(new
        {
            Chunks = 0,
            InputTokens = (long)GenerationEstimateModel.SetupInputTokens,
            OutputTokens = (long)GenerationEstimateModel.SetupOutputTokens,
        });
    }

    [Fact]
    public void A_sample_registry_string_table_is_one_chunk_of_minutes()
    {
        // Arrange
        var table = StringTableSerializer.Read(File.ReadAllBytes(Path.Combine(
            Fixtures, "registry", "games", "steam-620", "ru", "full", "strings", "strings.json.gz"))).Value!;
        var text = TextSize.Of(table.Strings.Values);

        // Act
        var estimate = GenerationEstimateModel.Estimate(text, EstimateBasis.Toolkit);

        // Assert
        estimate.Should().BeEquivalentTo(new { Text = new { Strings = 2, Characters = 18L }, Chunks = 1 });
        estimate.TimeHigh.Should().BeLessThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public void The_file_size_guess_caps_the_share_of_data_files()
    {
        // Arrange
        const long hugePacks = 100L * 1024 * 1024 * 1024;

        // Act
        var size = GenerationEstimateModel.GuessFromFileSizes(0, hugePacks);

        // Assert
        size.Characters.Should().Be(GenerationEstimateModel.MaxDataCharacters);
    }

    [Fact]
    public void The_file_size_guess_takes_a_share_of_text_files()
    {
        // Arrange
        const long textBytes = 4_000_000;

        // Act
        var size = GenerationEstimateModel.GuessFromFileSizes(textBytes, 0);

        // Assert
        size.Should().Be(new TextSize(1000, 6667, 40_000));
    }
}
