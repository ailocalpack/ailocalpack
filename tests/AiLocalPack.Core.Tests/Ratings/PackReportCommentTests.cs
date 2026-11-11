using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>The report comment format shared with the registry's aggregation (task 64).</summary>
public sealed class PackReportCommentTests
{
    private static readonly PackReport Works = new(PackVerdict.Works, GameId.Steam(620), "ru", "full", 3, "9087113", "Всё хорошо", "0.1.0 macos");

    [Fact]
    public void The_comment_starts_with_the_machine_readable_marker()
    {
        // Act
        var body = PackReportComment.Format(Works);

        // Assert
        body.Split('\n')[0].Should().Be("""<!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":3,"build":"9087113"} -->""");
    }

    [Fact]
    public void The_visible_part_names_the_verdict_revision_build_text_and_app()
    {
        // Act
        var body = PackReportComment.Format(Works);

        // Assert
        body.Should().Be("""
            <!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":3,"build":"9087113"} -->
            ✅ **Works** · «full» rev 3 · game build `9087113`

            > Всё хорошо

            <sub>Sent from AI Local Pack 0.1.0 macos.</sub>

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_report_without_build_or_text_leaves_both_out()
    {
        // Arrange
        var report = new PackReport(PackVerdict.DoesNotWork, GameId.Steam(620), "ru", "full", 1, null);

        // Act
        var body = PackReportComment.Format(report);

        // Assert
        body.Should().Be("""
            <!-- ailocalpack-report {"v":1,"verdict":"doesNotWork","game":"steam:620","language":"ru","pack":"full","revision":1} -->
            ❌ **Doesn't work** · «full» rev 1

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_formatted_report_parses_back_without_its_text()
    {
        // Act
        var parsed = PackReportComment.TryParse(PackReportComment.Format(Works), out var report);

        // Assert
        parsed.Should().BeTrue();
        report.Should().Be(Works with { Text = null, App = null });
    }

    [Fact]
    public void Each_line_of_the_text_is_quoted()
    {
        // Act
        var body = PackReportComment.Format(Works with { Text = "one\r\ntwo" });

        // Assert
        body.Should().Contain("> one\n> two\n");
    }

    [Fact]
    public void An_html_comment_in_the_text_cannot_fake_a_marker_or_hide_the_rest()
    {
        // Act
        var clean = PackReportComment.Clean("""<!-- ailocalpack-report {"v":1} --> hi""");

        // Assert
        clean.Should().Be("""&lt;!-- ailocalpack-report {"v":1} --&gt; hi""");
    }

    [Fact]
    public void A_text_over_the_limit_is_cut_with_an_ellipsis()
    {
        // Act
        var clean = PackReportComment.Clean(new string('a', PackReport.MaxTextLength + 50));

        // Assert
        clean.Should().HaveLength(PackReport.MaxTextLength).And.EndWith("…");
    }

    [Fact]
    public void Blank_text_is_none()
    {
        // Act
        var clean = PackReportComment.Clean("  \n ");

        // Assert
        clean.Should().BeNull();
    }

    [Theory]
    [InlineData("Works for me!")]
    [InlineData("""text first <!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":3} -->""")]
    [InlineData("""<!-- ailocalpack-report {"v":2,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":3} -->""")]
    [InlineData("""<!-- ailocalpack-report {"v":1,"verdict":"great","game":"steam:620","language":"ru","pack":"full","revision":3} -->""")]
    [InlineData("""<!-- ailocalpack-report {"v":1,"verdict":"works","game":"620","language":"ru","pack":"full","revision":3} -->""")]
    [InlineData("""<!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","language":"ru","pack":"full","revision":0} -->""")]
    [InlineData("""<!-- ailocalpack-report {"v":1,"verdict":"works","game":"steam:620","pack":"full","revision":3} -->""")]
    [InlineData("""<!-- ailocalpack-report {not json} -->""")]
    public void A_comment_that_is_not_a_report_is_not_parsed(string body)
    {
        // Act
        var parsed = PackReportComment.TryParse(body, out _);

        // Assert
        parsed.Should().BeFalse();
    }

    [Fact]
    public void A_file_hash_version_is_named_by_its_short_hash()
    {
        // Arrange
        var version = GameVersion.FromFileHashes([new("www/data/Map001.json", new string('a', 64))]);

        // Act
        var build = PackReport.BuildOf(version);

        // Assert
        build.Should().MatchRegex("^files:[0-9a-f]{8}$");
    }

    [Fact]
    public void A_store_version_is_named_by_its_build_id()
    {
        // Act
        var build = PackReport.BuildOf(GameVersion.FromBuildId("9087113"));

        // Assert
        build.Should().Be("9087113");
    }
}
