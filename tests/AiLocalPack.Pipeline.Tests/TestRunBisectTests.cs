using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Narrowing a crash down to the rebuilt file that causes it, with quick runs.</summary>
public sealed class TestRunBisectTests
{
    private static readonly string[] Files = ["a.json", "b.json", "c.json", "d.json", "e.json", "f.json", "g.json", "h.json"];

    private static TestRunResult Crash => TestRunResult.Crashed(TimeSpan.FromSeconds(6), 3, "crashed");

    private static TestRunResult Pass => TestRunResult.Passed(TimeSpan.FromSeconds(20));

    private static Task<TestRunBisectResult> RunAsync(FakeTestRunner runner, IReadOnlyList<string>? files = null, int maxRuns = TestRunBisect.DefaultMaxRuns) =>
        new TestRunBisect(runner, maxRuns).RunAsync(StageHarness.Game, "/game", "/job/build", files ?? Files, "/job", _ => { }, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_file_that_crashes_the_game_is_found()
    {
        // Arrange
        var runner = new FakeTestRunner { QuickResult = r => r.BuildFiles.Contains("f.json") ? Crash : Pass };

        // Act
        var result = await RunAsync(runner);

        // Assert
        result.Crashing.Should().Equal("f.json");
    }

    [Fact]
    public async Task Every_run_is_quick_and_the_halves_take_a_few_runs()
    {
        // Arrange
        var runner = new FakeTestRunner { QuickResult = r => r.BuildFiles.Contains("f.json") ? Crash : Pass };

        // Act
        var result = await RunAsync(runner);

        // Assert
        new { result.Runs, AllQuick = runner.Requests.All(r => r.Quick) }.Should().Be(new { Runs = 4, AllQuick = true });
    }

    [Fact]
    public async Task A_crash_that_needs_two_files_together_names_no_file()
    {
        // Arrange
        var runner = new FakeTestRunner { QuickResult = r => r.BuildFiles.Contains("a.json") && r.BuildFiles.Contains("h.json") ? Crash : Pass };

        // Act
        var result = await RunAsync(runner);

        // Assert
        new { result.Crashing, Says = result.Summary.StartsWith("No single file", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { Crashing = Array.Empty<string>(), Says = true });
    }

    [Fact]
    public async Task A_game_that_cant_be_started_stops_the_search()
    {
        // Arrange
        var runner = new FakeTestRunner();

        // Act
        var result = await RunAsync(runner);

        // Assert
        new { result.Runs, Crashing = result.Crashing.Count }.Should().Be(new { Runs = 1, Crashing = 0 });
    }

    [Fact]
    public async Task One_rebuilt_file_needs_no_run()
    {
        // Arrange
        var runner = new FakeTestRunner();

        // Act
        var result = await RunAsync(runner, ["lang/en.txt"]);

        // Assert
        new { result.Crashing, Runs = runner.Requests.Count }.Should().BeEquivalentTo(new { Crashing = new[] { "lang/en.txt" }, Runs = 0 });
    }

    [Fact]
    public async Task Out_of_runs_it_names_the_files_left()
    {
        // Arrange
        var runner = new FakeTestRunner { QuickResult = r => r.BuildFiles.Contains("f.json") ? Crash : Pass };

        // Act
        var result = await RunAsync(runner, maxRuns: 2);

        // Assert
        result.Crashing.Should().Equal("e.json", "f.json");
    }
}
