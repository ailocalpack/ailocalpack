using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace AiLocalPack.Desktop.Logging;

/// <summary>Rolling-file logging. Lives in the temp folder until app data folders arrive (task 04).</summary>
internal static class AppLogging
{
    public static string DefaultDirectory { get; } = Path.Combine(Path.GetTempPath(), "ailocalpack", "logs");

    public static ILoggerFactory CreateFactory(string directory)
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(directory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }
}
