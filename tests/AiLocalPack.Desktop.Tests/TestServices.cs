using AiLocalPack.Core.Registry;
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
/// is "not found" (an empty folder), or the <see cref="SteamFixture"/> a test builds. The registry is never the real
/// one: by default a local folder without an index (unavailable), or the address a test passes. No covers are loaded (no
/// network), UI updates run at once on the calling thread, folders are "opened" into <see cref="FakeShell"/> and the
/// OS pickers are <see cref="FakeFilePicker"/>.
/// </summary>
internal static class TestServices
{
    /// <param name="configure">Replaces services (a later registration wins), e.g. a fake <see cref="IAppTheming"/>.</param>
    /// <param name="steam">Builds Steam games; the library is refreshed once before the graph is returned.</param>
    /// <param name="registry">The registry index address (<c>AILOCALPACK_INDEX_URL</c>): a local folder or an https URL.</param>
    public static ServiceProvider Build(Action<IServiceCollection>? configure = null, Action<SteamFixture>? steam = null, string? registry = null)
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
            name => name switch
            {
                SteamLocator.RootVariable => steamRoot,
                RegistryLocation.IndexUrlVariable => registry ?? home.Combine("no-registry"),
                _ => null,
            });
        var provider = Composition.BuildServices(NullLoggerFactory.Instance, AppPaths.UnderHome(home.Path), env, services =>
        {
            // Created by a factory, so the provider disposes it.
            services.AddSingleton(_ => home);
            services.AddSingleton<ICoverLoader>(NoCoverLoader.Instance);
            services.AddSingleton<IUiDispatcher, ImmediateUiDispatcher>();
            services.AddSingleton<FakeShell>();
            services.AddSingleton<ISystemShell>(sp => sp.GetRequiredService<FakeShell>());
            services.AddSingleton<FakeFilePicker>();
            services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<FakeFilePicker>());
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

    public List<Uri> OpenedUrls { get; } = [];

    public bool OpenFolder(string path)
    {
        OpenedFolders.Add(path);
        return true;
    }

    public bool OpenUrl(Uri url)
    {
        OpenedUrls.Add(url);
        return true;
    }
}

/// <summary>Answers the OS pickers with what the test set (<c>null</c> = the player cancelled).</summary>
internal sealed class FakeFilePicker : IFilePicker
{
    public string? Folder { get; set; }

    public string? Image { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);

    public Task<string?> PickImageAsync(string title) => Task.FromResult(Image);
}
