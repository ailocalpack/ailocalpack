using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace AiLocalPack.Agents.Processes;

/// <summary>
/// The real <see cref="IProcessRunner"/>: no shell, no window, standard input / output / error redirected as UTF-8.
/// Killing takes the whole tree (<see cref="Process.Kill(bool)"/>: Windows, Linux and macOS walk the live descendants),
/// so an agent's own tool processes (MCP servers, shells) go with it.
/// </summary>
public sealed class ProcessRunner(ProcessRunnerOptions? options = null) : IProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ProcessRunnerOptions _options = options ?? ProcessRunnerOptions.Default;

    public IRunningProcess Start(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.WorkingDirectory is { } folder && !Directory.Exists(folder))
        {
            throw new ProcessStartException($"{folder}: no such folder to start {spec.FileName} in.");
        }

        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.Environment)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new ProcessStartException($"{spec.FileName}: the process didn't start.");
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new ProcessStartException($"{spec.FileName}: {e.Message}", e);
        }

        return new RunningProcess(process, spec.StandardInput, _options, cancellationToken);
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly ProcessRunnerOptions _options;
        private readonly Channel<ProcessLine> _lines = Channel.CreateUnbounded<ProcessLine>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<ProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        private int _killed;

        public RunningProcess(Process process, string? input, ProcessRunnerOptions options, CancellationToken cancellationToken)
        {
            _process = process;
            _options = options;
            Id = process.Id;
            var stdout = PumpAsync(process.StandardOutput, ProcessStream.StandardOutput);
            var stderr = PumpAsync(process.StandardError, ProcessStream.StandardError);
            _ = WriteInputAsync(process.StandardInput, input);
            _ = CompleteAsync(stdout, stderr);
            _registration = cancellationToken.Register(Kill);
        }

        public int Id { get; }

        public Task<ProcessExit> Exit => _exit.Task;

        public IAsyncEnumerable<ProcessLine> ReadLinesAsync(CancellationToken cancellationToken = default) =>
            _lines.Reader.ReadAllAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                if (_process.HasExited)
                {
                    return;
                }

                Interlocked.Exchange(ref _killed, 1);
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Exited meanwhile, or a descendant we may not kill; the rest of the tree is gone.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _registration.DisposeAsync();
            if (!_exit.Task.IsCompleted)
            {
                Kill();
            }

            await Task.WhenAny(_exit.Task, Task.Delay(DisposeWait));
            _process.Dispose();
        }

        private static async Task WriteInputAsync(StreamWriter writer, string? input)
        {
            try
            {
                if (input is not null)
                {
                    await writer.WriteAsync(input);
                    await writer.FlushAsync();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The process exited (or closed its input) before reading it all.
            }
            finally
            {
                try
                {
                    writer.Close();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // Broken pipe on close: nothing left to tell it.
                }
            }
        }

        private async Task PumpAsync(StreamReader reader, ProcessStream stream)
        {
            await Task.Yield();
            var buffer = new char[8192];
            var line = new StringBuilder();
            var truncated = false;
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var c = buffer[i];
                        if (c == '\n')
                        {
                            Emit(stream, line, truncated);
                            truncated = false;
                        }
                        else if (line.Length < _options.MaxLineLength)
                        {
                            line.Append(c);
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The pipe broke: the output ends here.
            }

            if (line.Length > 0 || truncated)
            {
                Emit(stream, line, truncated);
            }
        }

        private void Emit(ProcessStream stream, StringBuilder line, bool truncated)
        {
            var length = line.Length > 0 && line[^1] == '\r' && !truncated ? line.Length - 1 : line.Length;
            _lines.Writer.TryWrite(new ProcessLine(stream, line.ToString(0, length), truncated));
            line.Clear();
        }

        private async Task CompleteAsync(Task stdout, Task stderr)
        {
            var code = -1;
            try
            {
                await _process.WaitForExitAsync();
                await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(_options.OutputGrace));
                code = _process.ExitCode;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // The exit code couldn't be read; -1 stands for it.
            }
            finally
            {
                _lines.Writer.TryComplete();
                _exit.TrySetResult(new ProcessExit(code, Volatile.Read(ref _killed) == 1));
            }
        }
    }
}
