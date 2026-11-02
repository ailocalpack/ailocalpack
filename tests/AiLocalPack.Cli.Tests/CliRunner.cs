using System.Diagnostics;

namespace AiLocalPack.Cli.Tests;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs the built <c>ailocalpack</c> CLI in a child process, end to end.</summary>
internal static class CliRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    public static async Task<CliResult> RunAsync(params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "ailocalpack.dll");
        if (!File.Exists(cliDll))
        {
            throw new FileNotFoundException("CLI was not copied next to the tests.", cliDll);
        }

        // dotnet test sets DOTNET_HOST_PATH to the muxer that runs the tests.
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Timeout);

        var stdOut = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CliResult(process.ExitCode, await stdOut, await stdErr);
    }
}
