using AiLocalPack.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

/// <summary>The app's service graph for UI tests: no file logging, app folders in temp (never the player's real ones).</summary>
internal static class TestServices
{
    public static AppPaths Paths { get; } =
        AppPaths.UnderHome(Path.Combine(Path.GetTempPath(), "ailocalpack-tests", "desktop-home"));

    public static ServiceProvider Build() => Composition.BuildServices(NullLoggerFactory.Instance, Paths);
}
