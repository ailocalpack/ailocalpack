using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Toolkit.Recipes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

/// <summary>
/// The app's service graph for UI tests: no file logging, app folders and a fake home in a fresh temp folder per graph
/// (never the player's real ones), deleted when the provider is disposed. Steam is never the player's: by default it
/// is "not found" (an empty folder), or the <see cref="SteamFixture"/> a test builds. The registry is never the real
/// one: by default a local folder without an index (unavailable), or the address a test passes. No covers are loaded (no
/// network), UI updates run at once on the calling thread, folders are "opened" into <see cref="FakeShell"/>, the
/// OS pickers are <see cref="FakeFilePicker"/> and games are "started" into <see cref="FakeProcesses"/>.
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

                // Every toolkit engine on, whatever the OS the tests run on (RPG Maker is off on macOS).
                RecipeTools.AllEnginesVariable => "1",
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
            services.AddSingleton<FakeProcesses>();
            services.AddSingleton<IProcessList>(sp => sp.GetRequiredService<FakeProcesses>());
            services.AddSingleton<IProcessStarter>(sp => sp.GetRequiredService<FakeProcesses>());

            // Whitelisted fonts that aren't bundled come from a fake server (no route = the test fails loudly).
            services.AddSingleton<FontDownloads>();
            services.AddSingleton(sp => new FontStore(
                sp.GetRequiredService<AppPaths>().Fonts,
                sp.GetRequiredService<FontDownloads>().Http.CreateClient(),
                options: new PackFetchOptions(TimeSpan.FromSeconds(5), Attempts: 1)));
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

/// <summary>The fake server whitelisted fonts are downloaded from in tests.</summary>
internal sealed class FontDownloads : IDisposable
{
    public FakeHttpHandler Http { get; } = new();

    public void Dispose() => Http.Dispose();
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

/// <summary>A process list the test fills, recording what the app started instead of starting it.</summary>
internal sealed class FakeProcesses : IProcessList, IProcessStarter
{
    private readonly Lock _lock = new();
    private readonly List<RunningProcess> _running = [];

    public List<Uri> OpenedUrls { get; } = [];

    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Started { get; } = [];

    /// <summary>Thrown by the next start, as the OS refusing it.</summary>
    public Exception? Refuse { get; set; }

    public void Run(string path)
    {
        lock (_lock)
        {
            _running.Add(new RunningProcess(_running.Count + 100, path));
        }
    }

    public void ExitAll()
    {
        lock (_lock)
        {
            _running.Clear();
        }
    }

    public IReadOnlyList<RunningProcess> Snapshot()
    {
        lock (_lock)
        {
            return [.. _running];
        }
    }

    public void OpenUrl(Uri url)
    {
        ThrowIfRefused();
        OpenedUrls.Add(url);
    }

    public void Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ThrowIfRefused();
        Started.Add((fileName, arguments, workingDirectory));
    }

    private void ThrowIfRefused()
    {
        if (Refuse is { } e)
        {
            Refuse = null;
            throw e;
        }
    }
}

/// <summary>Installed packs the test sets (a broken pack, which the real store can't say until task 33).</summary>
internal sealed class FakeInstalledPacks : IInstalledPacks
{
    public event EventHandler<GameId>? Changed;

    public Dictionary<(GameId Game, string Language), InstalledPackState> Packs { get; } = [];

    /// <summary>Tells the statuses a pack changed, as the store does after an install.</summary>
    public void Raise(GameId game) => Changed?.Invoke(this, game);

    public InstalledPackState? Find(GameId game, string language) =>
        Packs.GetValueOrDefault((game, language));
}

/// <summary>Waits for work the app runs in the background (fire-and-forget commands).</summary>
internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds; fails the test after <paramref name="timeout"/> (10 s).</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition didn't hold in time.");
            }

            await Task.Delay(10);
        }
    }
}
