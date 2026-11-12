using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace AiLocalPack.Desktop.Tests.Build;

/// <summary>What the release workflow (task 71) relies on: the app's <c>--smoke-test</c> and the pinned <c>vpk</c>.</summary>
public sealed class ReleaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static string Release(string name) => Path.Combine(AppContext.BaseDirectory, "release", name);

    [Fact]
    public async Task Smoke_test_of_the_built_app_passes()
    {
        // Arrange
        var app = Path.Combine(AppContext.BaseDirectory, "AiLocalPack.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(app);
        start.ArgumentList.Add(SmokeTest.Argument);

        // Act
        var (exitCode, output) = await RunAsync(start);

        // Assert
        exitCode.Should().Be(0, output);
        output.Should().Contain("smoke test passed");
    }

    [Fact]
    public void Vpk_tool_is_the_version_of_the_Velopack_package()
    {
        // Arrange
        using var tools = JsonDocument.Parse(File.ReadAllText(Release("dotnet-tools.json")));
        var packages = XDocument.Load(Release("Directory.Packages.props"));

        // Act
        var vpk = tools.RootElement.GetProperty("tools").GetProperty("vpk").GetProperty("version").GetString();

        // Assert
        var velopack = packages.Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "Velopack");
        vpk.Should().Be((string?)velopack.Attribute("Version"), "vpk packs the release for the Velopack runtime in the app");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the app didn't start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output + await error);
    }
}
