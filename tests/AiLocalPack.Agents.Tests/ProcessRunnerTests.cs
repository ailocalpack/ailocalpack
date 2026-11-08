using System.Diagnostics;
using System.Globalization;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary><see cref="ProcessRunner"/> on a real child process, the fake agent program (task 40).</summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();
    private readonly List<int> _children = [];

    public void Dispose()
    {
        foreach (var id in _children)
        {
            try
            {
                using var child = Process.GetProcessById(id);
                child.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        _dir.Dispose();
    }

    private static ProcessSpec Spec(string recording, string? input = null, string? workingDirectory = null) =>
        new(Replayer.Executable, [.. Replayer.Arguments, recording]) { StandardInput = input, WorkingDirectory = workingDirectory };

    private static async Task<(List<ProcessLine> Lines, ProcessExit Exit)> RunAsync(IProcessRunner runner, ProcessSpec spec)
    {
        await using var process = runner.Start(spec, TestContext.Current.CancellationToken);
        var lines = new List<ProcessLine>();
        await foreach (var line in process.ReadLinesAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        return (lines, await process.Exit.WaitAsync(Wait, TestContext.Current.CancellationToken));
    }

    private static Task<(List<ProcessLine> Lines, ProcessExit Exit)> RunAsync(ProcessSpec spec) => RunAsync(new ProcessRunner(), spec);

    private static bool IsGone(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> GoneWithinAsync(int id)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (IsGone(id))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<int> ChildIdAsync(IRunningProcess process)
    {
        await foreach (var line in process.ReadLinesAsync(TestContext.Current.CancellationToken))
        {
            if (line.Stream == ProcessStream.StandardError && line.Text.StartsWith(FakeAgentReplay.ChildPidPrefix, StringComparison.Ordinal))
            {
                return int.Parse(line.Text[FakeAgentReplay.ChildPidPrefix.Length..], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("The fake agent named no child.");
    }

    [Fact]
    public async Task Standard_output_lines_arrive_in_order()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "first", "второй", "third");

        // Act
        var (lines, _) = await RunAsync(Spec(recording));

        // Assert
        lines.Should().Equal(
            new ProcessLine(ProcessStream.StandardOutput, "first"),
            new ProcessLine(ProcessStream.StandardOutput, "второй"),
            new ProcessLine(ProcessStream.StandardOutput, "third"));
    }

    [Fact]
    public async Task Standard_error_lines_are_captured_apart()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("stderr", "\"text\":\"warning: disk almost full\""));

        // Act
        var (lines, _) = await RunAsync(Spec(recording));

        // Assert
        lines.Should().Equal(new ProcessLine(ProcessStream.StandardError, "warning: disk almost full"));
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_reported()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "out", Control("exit", "\"code\":3"));

        // Act
        var (_, exit) = await RunAsync(Spec(recording));

        // Assert
        exit.Should().Be(new ProcessExit(3, Killed: false));
    }

    [Fact]
    public async Task Standard_input_reaches_the_process_as_utf8()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("prompt"));

        // Act
        var (lines, _) = await RunAsync(Spec(recording, input: "Переведи «это»"));

        // Assert
        lines.Should().Equal(new ProcessLine(ProcessStream.StandardOutput, Event(new AgentMessage("Переведи «это»"))));
    }

    [Fact]
    public async Task The_process_starts_in_the_working_directory()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("cwd"));
        var folder = _dir.Dir("work").Combine("work");

        // Act
        var (lines, _) = await RunAsync(Spec(recording, workingDirectory: folder));

        // Assert
        lines.Should().Equal(new ProcessLine(ProcessStream.StandardOutput, Event(new AgentMessage(Core.Storage.PathNames.RealPath(folder)))));
    }

    [Fact]
    public async Task A_line_longer_than_the_limit_is_cut_and_marked()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", new string('x', 100), "next");
        var runner = new ProcessRunner(ProcessRunnerOptions.Default with { MaxLineLength = 10 });

        // Act
        var (lines, _) = await RunAsync(runner, Spec(recording));

        // Assert
        lines.Should().Equal(
            new ProcessLine(ProcessStream.StandardOutput, new string('x', 10), Truncated: true),
            new ProcessLine(ProcessStream.StandardOutput, "next"));
    }

    [Fact]
    public async Task Cancelling_kills_the_process()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "started", Control("hang"));
        using var cancel = new CancellationTokenSource();
        await using var process = new ProcessRunner().Start(Spec(recording), cancel.Token);
        await foreach (var _ in process.ReadLinesAsync(TestContext.Current.CancellationToken))
        {
            break;
        }

        // Act
        await cancel.CancelAsync();

        // Assert
        (await process.Exit.WaitAsync(Wait, TestContext.Current.CancellationToken)).Killed.Should().BeTrue();
    }

    [Fact]
    public async Task Kill_takes_the_processes_it_started_too()
    {
        // Arrange
        var child = Write(_dir, "child.jsonl", Control("hang"));
        var recording = Write(_dir, "r.jsonl", Control("child", $"\"recording\":{System.Text.Json.JsonSerializer.Serialize(child)}"), Control("hang"));
        await using var process = new ProcessRunner().Start(Spec(recording), TestContext.Current.CancellationToken);
        var childId = await ChildIdAsync(process);
        _children.Add(childId);

        // Act
        process.Kill();

        // Assert
        (await GoneWithinAsync(childId)).Should().BeTrue();
    }

    [Fact]
    public async Task The_run_ends_after_the_grace_when_a_left_over_child_holds_the_output()
    {
        // Arrange
        var child = Write(_dir, "child.jsonl", Control("hang"));
        var recording = Write(_dir, "r.jsonl", Control("child", $"\"recording\":{System.Text.Json.JsonSerializer.Serialize(child)}"), Control("delay", "\"ms\":300"));
        var runner = new ProcessRunner(ProcessRunnerOptions.Default with { OutputGrace = TimeSpan.FromMilliseconds(200) });
        await using var process = runner.Start(Spec(recording), TestContext.Current.CancellationToken);
        _children.Add(await ChildIdAsync(process));

        // Act
        var exit = await process.Exit.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(new ProcessExit(0, Killed: false));
    }

    [Fact]
    public void A_missing_program_cannot_start()
    {
        // Arrange
        var spec = new ProcessSpec(_dir.Combine("no-such-agent"), []);

        // Act
        var act = () => new ProcessRunner().Start(spec, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ProcessStartException>();
    }

    [Fact]
    public void A_missing_working_directory_cannot_start()
    {
        // Arrange
        var spec = Spec(Write(_dir, "r.jsonl", "x"), workingDirectory: _dir.Combine("gone"));

        // Act
        var act = () => new ProcessRunner().Start(spec, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ProcessStartException>().WithMessage("*gone*");
    }

    [Fact]
    public async Task Run_to_end_collects_both_outputs()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", "1.0.42 (Fake Agent)", Control("stderr", "\"text\":\"note\""));

        // Act
        var result = await new ProcessRunner().RunToEndAsync(Spec(recording), Wait, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new ProcessResult(0, "1.0.42 (Fake Agent)", "note"));
    }

    [Fact]
    public async Task Run_to_end_kills_a_process_past_its_time_limit()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("hang"));

        // Act
        var result = await new ProcessRunner().RunToEndAsync(Spec(recording), TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        // Assert
        result.TimedOut.Should().BeTrue();
    }
}
