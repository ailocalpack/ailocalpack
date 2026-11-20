using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// Where the stores beside Steam keep their records on this OS (task 83). Everything comes from the injected
/// <see cref="OsEnvironment"/> and <see cref="IStoreRegistry"/>; <see cref="RootVariable"/> moves every place under one
/// folder (dev, tests, manual checks), with the registry read from <c>&lt;root&gt;/registry.json</c>
/// (<see cref="FileStoreRegistry"/>).
/// </summary>
public sealed class StoreLocations
{
    /// <summary>
    /// Read every store from this folder instead (like <c>AILOCALPACK_STEAM_ROOT</c>): <c>epic-launcher/Manifests</c>,
    /// <c>heroic</c>, <c>legendary</c>, <c>Applications</c> (GOG app bundles) and <c>registry.json</c>; Windows-only stores
    /// are read on every OS then.
    /// </summary>
    public const string RootVariable = "AILOCALPACK_STORES_ROOT";

    private readonly OsEnvironment _env;
    private readonly string? _root;

    public StoreLocations(OsEnvironment env, IStoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(registry);
        _env = env;
        _root = env.GetVariable(RootVariable) is { Length: > 0 } root ? root : null;
        Registry = _root is null ? registry : new FileStoreRegistry(Path.Combine(_root, "registry.json"));
    }

    /// <summary>For the running app: the machine's registry, or the file under <see cref="RootVariable"/>.</summary>
    public StoreLocations(OsEnvironment env)
        : this(env, WindowsStoreRegistry.Instance)
    {
    }

    public IStoreRegistry Registry { get; }

    public OsKind Os => _env.Os;

    /// <summary>The stores are read from <see cref="RootVariable"/>'s folder (Windows-only stores too).</summary>
    public bool IsOverridden => _root is not null;

    /// <summary>The Epic Games Launcher's folders of <c>*.item</c> manifests (Windows, macOS), most likely first.</summary>
    public IReadOnlyList<string> EpicManifestFolders()
    {
        if (_root is not null)
        {
            return [Path.Combine(_root, "epic-launcher", "Manifests")];
        }

        return _env.Os switch
        {
            OsKind.Windows => Distinct(
                Registry.GetString(RegistryHiveKind.CurrentUser, @"Software\Epic Games\EOS", "ModSdkMetadataDir", registry32: false),
                Path.Combine(_env.GetVariable("ProgramData") ?? @"C:\ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests")),
            OsKind.MacOS => [Path.Combine(_env.HomeDirectory, "Library", "Application Support", "Epic", "EpicGamesLauncher", "Data", "Manifests")],
            _ => [],
        };
    }

    /// <summary>Heroic Games Launcher's config folders (<c>…/heroic</c>): native first, then Flatpak on Linux.</summary>
    public IReadOnlyList<string> HeroicFolders()
    {
        if (_root is not null)
        {
            return [Path.Combine(_root, "heroic")];
        }

        return _env.Os switch
        {
            OsKind.Windows => [Path.Combine(_env.GetVariable("APPDATA") ?? Path.Combine(_env.HomeDirectory, "AppData", "Roaming"), "heroic")],
            OsKind.MacOS => [Path.Combine(_env.HomeDirectory, "Library", "Application Support", "heroic")],
            OsKind.Linux => Distinct(
                Path.Combine(ConfigHome(), "heroic"),
                Path.Combine(_env.HomeDirectory, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic")),
            _ => [],
        };
    }

    /// <summary>Standalone Legendary's config folder (<c>~/.config/legendary</c> on every OS).</summary>
    public IReadOnlyList<string> LegendaryFolders() =>
        _root is not null ? [Path.Combine(_root, "legendary")]
        : _env.Os == OsKind.Linux ? [Path.Combine(ConfigHome(), "legendary")]
        : [Path.Combine(_env.HomeDirectory, ".config", "legendary")];

    /// <summary>Folders of macOS app bundles GOG Galaxy installs into (<c>/Applications</c>, <c>~/Applications</c>).</summary>
    public IReadOnlyList<string> GogAppFolders() =>
        _root is not null ? [Path.Combine(_root, "Applications")]
        : _env.Os == OsKind.MacOS ? ["/Applications", Path.Combine(_env.HomeDirectory, "Applications")]
        : [];

    private string ConfigHome() =>
        _env.GetVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
            ? config
            : Path.Combine(_env.HomeDirectory, ".config");

    private static IReadOnlyList<string> Distinct(params string?[] paths) =>
        [.. paths.OfType<string>().Where(p => p.Length > 0).Distinct(PathNames.Comparer)];
}

/// <summary>The stores beside Steam, in the order Settings → Libraries lists them.</summary>
public static class GameStores
{
    public static IReadOnlyList<IGameStore> Create(StoreLocations locations, Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        logs ??= Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return
        [
            new EpicStore(locations, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<EpicStore>(logs)),
            new GogStore(locations, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<GogStore>(logs)),
            new EaAppStore(locations, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<EaAppStore>(logs)),
        ];
    }
}
