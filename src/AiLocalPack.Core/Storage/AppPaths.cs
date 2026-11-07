namespace AiLocalPack.Core.Storage;

/// <summary>
/// Where the app keeps its files. Three roots per OS: <see cref="Config"/> (settings, small, may roam),
/// <see cref="Data"/> (logs, backups, working copies: large, machine-local) and <see cref="Cache"/> (safe to delete).
/// Resolve with <see cref="Resolve"/>; nothing is created on disk here.
/// </summary>
public sealed record AppPaths
{
    /// <summary>Folder name under each per-OS root.</summary>
    public const string AppFolderName = "ailocalpack";

    /// <summary>Puts all three roots under one folder (<c>config/</c>, <c>data/</c>, <c>cache/</c>): dev, tests, portable installs.</summary>
    public const string HomeVariable = "AILOCALPACK_HOME";

    public AppPaths(string config, string data, string cache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(cache);
        Config = config;
        Data = data;
        Cache = cache;
    }

    public string Config { get; }

    public string Data { get; }

    public string Cache { get; }

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string Logs => Path.Combine(Data, "logs");

    /// <summary>The game catalog (machine-local: holds install paths).</summary>
    public string GamesFile => Path.Combine(Data, "games.json");

    /// <summary>Originals of game files overwritten by installed packs (what makes uninstall possible).</summary>
    public string Backups => Path.Combine(Data, "backups");

    /// <summary>The installed packs: per game the pack, revision, touched files with their original and new hashes, backup folder.</summary>
    public string InstalledPacksFile => Path.Combine(Data, "installed-packs.json");

    /// <summary>
    /// The install transaction in progress (<see cref="Install.PackInstaller"/>); one left behind by a crash is finished
    /// on the next start.
    /// </summary>
    public string InstallJournal => Path.Combine(Data, "install-journal.json");

    /// <summary>Held while an install, re-apply or uninstall runs, so only one runs at a time across app and CLI.</summary>
    public string InstallLock => Path.Combine(Data, "install.lock");

    /// <summary>Copies of game files the agent works on during generation.</summary>
    public string WorkingCopies => Path.Combine(Data, "work");

    /// <summary>
    /// Covers of manually added games: the image the player picked or the game's icon (in data, not cache: a picked
    /// image can't be fetched again).
    /// </summary>
    public string GameCovers => Path.Combine(Data, "covers");

    /// <summary>Game covers downloaded from the Steam CDN (safe to delete: fetched again when missing).</summary>
    public string Covers => Path.Combine(Cache, "covers");

    /// <summary>
    /// The last registry index downloaded (<c>index.json</c> + <c>index.meta.json</c>), shown when the registry can't be
    /// reached (safe to delete: downloaded again).
    /// </summary>
    public string RegistryCache => Path.Combine(Cache, "registry");

    /// <summary>
    /// Downloaded packs (<see cref="Packs.PackCache"/>): manifest, string table and binary patch per pack revision
    /// (safe to delete: downloaded again).
    /// </summary>
    public string Packs => Path.Combine(Cache, "packs");

    /// <summary>
    /// Whitelisted fonts downloaded on demand (<see cref="Fonts.FontStore"/>: the CJK ones; safe to delete: downloaded
    /// again when a pack needs them).
    /// </summary>
    public string Fonts => Path.Combine(Cache, "fonts");

    /// <summary>
    /// Downloaded release archives of whitelisted dependencies (<c>&lt;id&gt;/&lt;version&gt;/&lt;first 16 hex of the SHA-256&gt;-&lt;file&gt;</c>;
    /// safe to delete: downloaded again when an install needs them).
    /// </summary>
    public string Dependencies => Path.Combine(Cache, "dependencies");

    /// <summary>Remembered SHA-256 of game files (<see cref="FileHashCache"/>; safe to delete: files are hashed again).</summary>
    public string FileHashes => Path.Combine(Cache, "file-hashes.json");

    /// <summary>All three roots under <paramref name="home"/> (made absolute against the current directory).</summary>
    public static AppPaths UnderHome(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var root = Path.GetFullPath(home);
        return new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "cache"));
    }

    /// <summary>
    /// <c>AILOCALPACK_HOME</c> if set; otherwise Windows <c>%APPDATA%</c> (config) and <c>%LOCALAPPDATA%</c> (data, cache);
    /// macOS <c>~/Library/Application Support</c> (config, data) and <c>~/Library/Caches</c>; Linux XDG
    /// (<c>$XDG_CONFIG_HOME</c>, <c>$XDG_DATA_HOME</c>, <c>$XDG_CACHE_HOME</c>, relative values ignored as the spec says).
    /// </summary>
    public static AppPaths Resolve(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.GetVariable(HomeVariable) is { } home)
        {
            return UnderHome(home);
        }

        var userHome = env.HomeDirectory;
        return env.Os switch
        {
            OsKind.Windows => FromWindows(env, userHome),
            OsKind.MacOS => FromMacOS(userHome),
            OsKind.Linux => FromXdg(env, userHome),
            _ => throw new ArgumentOutOfRangeException(nameof(env), env.Os, "Unknown OS."),
        };
    }

    private static AppPaths FromWindows(OsEnvironment env, string userHome)
    {
        var roaming = env.GetVariable("APPDATA") ?? Path.Combine(userHome, "AppData", "Roaming");
        var local = env.GetVariable("LOCALAPPDATA") ?? Path.Combine(userHome, "AppData", "Local");
        var data = Path.Combine(local, AppFolderName);
        return new AppPaths(Path.Combine(roaming, AppFolderName), data, Path.Combine(data, "cache"));
    }

    private static AppPaths FromMacOS(string userHome)
    {
        var library = Path.Combine(userHome, "Library");
        var support = Path.Combine(library, "Application Support", AppFolderName);
        return new AppPaths(support, support, Path.Combine(library, "Caches", AppFolderName));
    }

    private static AppPaths FromXdg(OsEnvironment env, string userHome)
    {
        string Xdg(string variable, params string[] fallback) =>
            env.GetVariable(variable) is { } value && Path.IsPathRooted(value)
                ? value
                : Path.Combine([userHome, .. fallback]);

        return new AppPaths(
            Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), AppFolderName),
            Path.Combine(Xdg("XDG_DATA_HOME", ".local", "share"), AppFolderName),
            Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), AppFolderName));
    }
}
