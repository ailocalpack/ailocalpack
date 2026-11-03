using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

/// <summary>
/// The app's service graph for UI tests: no file logging, app folders and a fake home in a fresh temp folder per graph
/// (never the player's real ones), deleted when the provider is disposed. Steam is never the player's: by default it
/// is "not found" (an empty folder), or the <see cref="SteamFixture"/> a test builds. No covers are loaded (no
/// network), UI updates run at once on the calling thread, and folders are "opened" into <see cref="FakeShell"/>.
/// </summary>
internal static class TestServices
{
    /// <param name="configure">Replaces services (a later registration wins), e.g. a fake <see cref="IAppTheming"/>.</param>
    /// <param name="steam">Builds Steam games; the library is refreshed once before the graph is returned.</param>
    public static ServiceProvider Build(Action<IServiceCollection>? configure = null, Action<SteamFixture>? steam = null)
    {
        var home = new TempDir();
        string steamRoot;
        if (steam is null)
        {
            steamRoot = home.Dir("no-steam").Combine("no-steam");
        }
        else
        {
            var fixture = new SteamFixture(home, "Steam");
            steam(fixture);
            steamRoot = fixture.Root;
        }

        var env = new OsEnvironment(
            OsEnvironment.Current.Os,
            home.Dir("user").Combine("user"),
            name => name == SteamLocator.RootVariable ? steamRoot : null);
        var provider = Composition.BuildServices(NullLoggerFactory.Instance, AppPaths.UnderHome(home.Path), env, services =>
        {
            // Created by a factory, so the provider disposes it.
            services.AddSingleton(_ => home);
            services.AddSingleton<ICoverLoader>(NoCoverLoader.Instance);
            services.AddSingleton<IUiDispatcher, ImmediateUiDispatcher>();
            services.AddSingleton<FakeShell>();
            services.AddSingleton<ISystemShell>(sp => sp.GetRequiredService<FakeShell>());
            configure?.Invoke(services);
        });
        provider.GetRequiredService<TempDir>();
        if (steam is not null)
        {
            provider.GetRequiredService<GameLibrary>().Refresh();
        }

        return provider;
    }
}

/// <summary>Runs UI updates right away on the calling thread.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Records the folders the app asked the OS to open.</summary>
internal sealed class FakeShell : ISystemShell
{
    public List<string> OpenedFolders { get; } = [];

    public bool OpenFolder(string path)
    {
        OpenedFolders.Add(path);
        return true;
    }
}
