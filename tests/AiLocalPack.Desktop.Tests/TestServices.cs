using AiLocalPack.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

/// <summary>
/// The app's service graph for UI tests: no file logging, app folders in a fresh temp folder per graph (never the
/// player's real ones), deleted when the provider is disposed.
/// </summary>
internal static class TestServices
{
    /// <param name="configure">Replaces services (a later registration wins), e.g. a fake <see cref="IAppTheming"/>.</param>
    public static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var home = new TempDir();
        var provider = Composition.BuildServices(NullLoggerFactory.Instance, AppPaths.UnderHome(home.Path), services =>
        {
            // Created by a factory, so the provider disposes it.
            services.AddSingleton(_ => home);
            configure?.Invoke(services);
        });
        provider.GetRequiredService<TempDir>();
        return provider;
    }
}
