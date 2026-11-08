using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AiLocalPack.Agents.Fake;

/// <summary>
/// The fake agent's program (task 40): replays a recording to standard output, line by line, so agent and pipeline tests
/// run a real child process without a real agent. Hosted by the test program <c>AiLocalPack.FakeAgent</c> and by the
/// hidden CLI command <c>agent replay</c>.
/// <para>
/// A recording is a JSONL file. Every line is written as is (events in <see cref="AgentEventJson"/> format, or anything
/// else to test malformed output), except control lines, objects with a <c>control</c> property:
/// <c>{"control":"delay","ms":100}</c> waits; <c>{"control":"stderr","text":"…"}</c> writes a line to standard error;
/// <c>{"control":"prompt"}</c> writes a message event with the text read from standard input (the prompt);
/// <c>{"control":"cwd"}</c> a message event with the working folder; <c>{"control":"exit","code":3}</c> stops with that exit
/// code (the end of the file = 0); <c>{"control":"hang"}</c> waits until killed;
/// <c>{"control":"child","recording":"…"}</c> starts another replayer on that recording (sharing this one's outputs) and
/// writes <c>child-pid &lt;pid&gt;</c> to standard error, for process-tree tests.
/// </para>
/// </summary>
public static class FakeAgentReplay
{
    /// <summary>The standard error line naming a started child's process ID.</summary>
    public const string ChildPidPrefix = "child-pid ";

    /// <summary>
    /// Replays <paramref name="recording"/> on the process's own standard streams as UTF-8 (whatever the console code page),
    /// children started through <paramref name="replayer"/>.
    /// </summary>
    public static async Task<int> RunOnConsoleAsync(string recording, FakeAgentReplayer replayer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replayer);
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        await using var error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        return await RunAsync(recording, input, output, error, replayer.StartInfo, cancellationToken);
    }

    /// <summary>
    /// Replays <paramref name="recording"/>; returns the exit code (2 when the recording can't be read or a control line is
    /// wrong). <paramref name="startChild"/> gives the start info of a replayer for another recording.
    /// </summary>
    public static async Task<int> RunAsync(
        string recording,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, ProcessStartInfo> startChild,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(startChild);
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(recording, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"{recording}: {e.Message}");
            return 2;
        }

        var number = 0;
        foreach (var line in lines)
        {
            number++;
            var control = Control(line);
            if (control is null)
            {
                await output.WriteLineAsync(line);
                await output.FlushAsync(cancellationToken);
                continue;
            }

            try
            {
                var (name, element) = control.Value;
                switch (name)
                {
                    case "delay":
                        await Task.Delay(element.GetProperty("ms").GetInt32(), cancellationToken);
                        break;
                    case "stderr":
                        await error.WriteLineAsync(element.GetProperty("text").GetString());
                        await error.FlushAsync(cancellationToken);
                        break;
                    case "prompt":
                        await Write(output, new AgentMessage(await input.ReadToEndAsync(cancellationToken)), cancellationToken);
                        break;
                    case "cwd":
                        await Write(output, new AgentMessage(Directory.GetCurrentDirectory()), cancellationToken);
                        break;
                    case "exit":
                        return element.GetProperty("code").GetInt32();
                    case "hang":
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        break;
                    case "child":
                        var path = element.GetProperty("recording").GetString()!;
                        using (var child = Process.Start(startChild(path)) ?? throw new InvalidOperationException("the child didn't start"))
                        {
                            await error.WriteLineAsync(ChildPidPrefix + child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            await error.FlushAsync(cancellationToken);
                        }

                        break;
                    default:
                        throw new FormatException($"unknown control \"{name}\"");
                }
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or Win32Exception)
            {
                await error.WriteLineAsync($"{recording}:{number}: {e.Message}");
                return 2;
            }
        }

        return 0;
    }

    private static async Task Write(TextWriter output, AgentEvent e, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(AgentEventJson.Write(e));
        await output.FlushAsync(cancellationToken);
    }

    private static (string Name, JsonElement Element)? Control(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("control", out var control)
                && control.ValueKind == JsonValueKind.String
                    ? (control.GetString()!, document.RootElement.Clone())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
