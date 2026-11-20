using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Toolkit.Generation;

/// <summary>
/// What the OS recorded when a game crashed, so the player doesn't copy it by hand: on Windows the Application log's
/// <i>Application Error</i> events (event 1000: faulting module, exception code, offset) for the program, read with
/// <c>wevtutil</c>; on macOS the newest <c>~/Library/Logs/DiagnosticReports/&lt;program&gt;*.ips|.crash</c>; on Linux
/// nothing yet (the exit code and signal already tell). Best effort, never throws; only reports since
/// <c>since</c> (a minute's slack) count.
/// </summary>
public static class CrashReports
{
    private const int MaxLines = 80;
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The report of <paramref name="program"/> (a file name like <c>barony.exe</c> or <c>Barony.app</c>), or <c>null</c>.</summary>
    public static GameLogTail? Find(OsEnvironment environment, string program, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        try
        {
            return environment.Os switch
            {
                OsKind.Windows when OperatingSystem.IsWindows() => Windows(Path.GetFileName(program), since - Slack),
                OsKind.MacOS => Mac(environment.HomeDirectory, Path.GetFileNameWithoutExtension(program), since - Slack),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The fault of a Windows <i>Application Error</i> event (its <c>wevtutil /f:text</c> lines: <c>Faulting module name:</c>,
    /// <c>Exception code:</c>, <c>Fault offset:</c>); <c>null</c> when the lines don't name a module (another OS, another
    /// display language of Windows).
    /// </summary>
    /// <param name="program">The game's program (file name), to tell its own code from a library.</param>
    public static CrashFault? Fault(IReadOnlyList<string> lines, string program)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? Value(string label) => lines
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(label, StringComparison.OrdinalIgnoreCase))?[label.Length..].Trim();

        if (Value("Faulting module name:") is not { Length: > 0 } module)
        {
            return null;
        }

        var parts = module.Split(',', StringSplitOptions.TrimEntries);
        var name = parts[0];
        var version = parts.Skip(1).FirstOrDefault(p => p.StartsWith("version:", StringComparison.OrdinalIgnoreCase))?["version:".Length..].Trim();
        var code = Value("Exception code:") is { Length: > 0 } c ? Hex(c) : null;
        var offset = Value("Fault offset:") is { Length: > 0 } o ? Hex(o) : null;
        // A Windows event's program path, read on any OS: Path.GetFileName splits on '\' only on Windows.
        var inGame = string.Equals(name, program[(program.LastIndexOfAny(['/', '\\']) + 1)..], StringComparison.OrdinalIgnoreCase);
        return new CrashFault(name, version, code, offset, inGame);
    }

    /// <summary><c>0x0000000000a3b505</c> → <c>0xa3b505</c>; other text as it is.</summary>
    private static string Hex(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2
            ? "0x" + (text[2..].TrimStart('0') is { Length: > 0 } digits ? digits.ToLowerInvariant() : "0")
            : text;

    /// <summary>A Windows exit code as Windows writes it (<c>0xC0000409</c>) when it is an NTSTATUS, else the number.</summary>
    public static string ExitCodeText(int exitCode) => exitCode < 0
        ? "0x" + ((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture) + (Status((uint)exitCode) is { } name ? $" ({name})" : "")
        : exitCode.ToString(CultureInfo.InvariantCulture);

    private static string? Status(uint code) => code switch
    {
        0xC0000005 => "access violation",
        0xC0000409 => "stack buffer overrun / fail-fast",
        0xC00000FD => "stack overflow",
        0xC0000374 => "heap corruption",
        0xC0000135 => "a DLL is missing",
        0xC000001D => "illegal instruction",
        0xC0000094 => "division by zero",
        0xE06D7363 => "unhandled C++ exception",
        _ => null,
    };

    private static GameLogTail? Windows(string program, DateTimeOffset after)
    {
        var time = after.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var query = $"*[System[Provider[@Name='Application Error'] and TimeCreated[@SystemTime>='{time}']]]";
        var start = new ProcessStartInfo("wevtutil")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "qe", "Application", $"/q:{query}", "/f:text", "/rd:true", "/c:20" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        var events = output.GetAwaiter().GetResult().ReplaceLineEndings("\n").Split("Event[", StringSplitOptions.RemoveEmptyEntries);
        var match = events.FirstOrDefault(e => e.Contains(program, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var lines = ("Event[" + match).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(MaxLines).ToList();
        return new GameLogTail("Windows Application log (Application Error)", lines);
    }

    private static GameLogTail? Mac(string home, string program, DateTimeOffset after)
    {
        var folder = Path.Combine(home, "Library", "Logs", "DiagnosticReports");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var newest = new DirectoryInfo(folder).EnumerateFiles(program + "*")
            .Where(f => f.Extension is ".ips" or ".crash" && f.LastWriteTimeUtc >= after.UtcDateTime)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null)
        {
            return null;
        }

        var lines = File.ReadLines(newest.FullName).Take(MaxLines).ToList();
        return new GameLogTail(newest.FullName, lines);
    }
}
