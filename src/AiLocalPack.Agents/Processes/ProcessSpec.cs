namespace AiLocalPack.Agents.Processes;

/// <summary>A program to start without a shell: arguments are passed as a list, so the OS quotes them (task 40).</summary>
/// <param name="FileName">The executable (absolute path, or a name the OS looks up on <c>PATH</c>).</param>
/// <param name="Arguments">Its arguments, one item each.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Folder the process starts in; <c>null</c> = the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Changes to the inherited environment: a value sets the variable, <c>null</c> removes it.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Text written to the process's standard input as UTF-8 (no BOM), after which the input is closed; <c>null</c> =
    /// closed at once. A long prompt goes here rather than on the command line (Windows caps it at 32 767 characters).
    /// </summary>
    public string? StandardInput { get; init; }

    /// <summary>
    /// Windows only: the whole command line after the program, passed as is instead of quoting <see cref="Arguments"/>
    /// (which must then be empty). For <c>cmd.exe /d /s /c "…"</c>, whose quoting rules aren't the C runtime's
    /// (<see cref="WindowsCommandLine.ForBatchFile"/>).
    /// </summary>
    public string? RawArguments { get; init; }
}

/// <summary>Which output a line came from.</summary>
public enum ProcessStream
{
    StandardOutput,
    StandardError,
}

/// <summary>One line of a process's output, without its line ending.</summary>
/// <param name="Stream">Standard output or error.</param>
/// <param name="Text">The line (cut at <see cref="ProcessRunnerOptions.MaxLineLength"/> characters).</param>
/// <param name="Truncated"><c>true</c> when the line was longer than the limit and the rest of it was dropped.</param>
public sealed record ProcessLine(ProcessStream Stream, string Text, bool Truncated = false);

/// <summary>How a process ended.</summary>
/// <param name="ExitCode">Its exit code (on Unix a process killed by a signal reports 128 + the signal).</param>
/// <param name="Killed"><c>true</c> when it was killed through <see cref="IRunningProcess.Kill"/>, cancellation or disposal.</param>
public sealed record ProcessExit(int ExitCode, bool Killed);

/// <summary>The program couldn't be started (not found, not executable, bad working folder).</summary>
public sealed class ProcessStartException : Exception
{
    public ProcessStartException()
    {
    }

    public ProcessStartException(string message)
        : base(message)
    {
    }

    public ProcessStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Limits of a <see cref="ProcessRunner"/>.</summary>
/// <param name="MaxLineLength">Longest line kept, in characters; the rest of a longer one is dropped (8 Mi by default).</param>
/// <param name="OutputGrace">
/// How long to wait for the output to end after the process exited: a grandchild that escaped the kill can hold the pipes
/// open, and the run must still end.
/// </param>
public sealed record ProcessRunnerOptions(int MaxLineLength = 8 * 1024 * 1024, TimeSpan OutputGrace = default)
{
    public static ProcessRunnerOptions Default { get; } = new(OutputGrace: TimeSpan.FromSeconds(2));
}
