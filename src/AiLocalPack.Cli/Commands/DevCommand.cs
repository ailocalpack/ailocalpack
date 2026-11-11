using System.CommandLine;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Dependencies;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes.Agent;
using AiLocalPack.Toolkit.Recipes.Godot;
using AiLocalPack.Toolkit.Recipes.RenPy;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using AiLocalPack.Toolkit.Recipes.Unity;
using AiLocalPack.Toolkit.Recipes.Unreal;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Developer commands for checking the install transaction by hand (task 27): <c>dev apply-files &lt;game&gt; &lt;dir&gt;</c>
/// installs the files of a folder (laid out like the game folder) as a pack, <c>dev uninstall &lt;game&gt;</c> undoes it.
/// <c>--dependency id@version</c> installs whitelisted dependencies with them (task 32). Both first finish a transaction a crash left behind. Exit code 1 = refused or failed (files restored).
/// <c>dev fixture rpgmaker|renpy|godot|unity|unreal|custom &lt;dir&gt;</c> write synthetic games (tasks 28, 30, 35, 57).
/// </summary>
internal static class DevCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("dev", "Developer tools: install a folder of files into a game and undo it, the way packs are installed.")
        {
            BuildApply(env),
            BuildUninstall(env),
            BuildFixture(),
        };
    }

    private static Command BuildApply(OsEnvironment env)
    {
        var gameArgument = new Argument<string>("game") { Description = "The game (steam:<app id> or fp:<fingerprint>), as listed by 'games list'." };
        var dirArgument = new Argument<string>("dir") { Description = "A folder laid out like the game folder: each file in it replaces (or adds) the same path in the game." };
        var pack = new Option<string>("--pack") { Description = "Pack ID to record.", DefaultValueFactory = _ => "dev" };
        var lang = new Option<string>("--lang") { Description = "Language to record.", DefaultValueFactory = _ => "en" };
        var revision = new Option<int>("--revision") { Description = "Revision to record.", DefaultValueFactory = _ => 1 };
        var reapply = new Option<bool>("--reapply") { Description = "Install over the game's installed pack (as after a game update wiped it)." };
        var dependency = new Option<string[]>("--dependency")
        {
            Description = "A whitelisted dependency to install with the files, as <id>@<version> (from the registry's list; repeatable).",
            AllowMultipleArgumentsPerToken = false,
        };
        var apply = new Command(
            "apply-files",
            "Install the files of a folder into a game as one transaction: originals backed up, new files written and checked, everything restored on failure.")
        {
            gameArgument, dirArgument, pack, lang, revision, reapply, dependency,
        };
        apply.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(gameArgument)!, error) is not var (installer, game, index))
            {
                return 1;
            }

            var dir = Path.GetFullPath(result.GetValue(dirArgument)!);
            if (!Directory.Exists(dir))
            {
                error.WriteLine($"{dir} is not a folder.");
                return 1;
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new InstallFile(Path.GetRelativePath(dir, f).Replace('\\', '/'), f))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                error.WriteLine($"{dir} has no files.");
                return 1;
            }

            WriteRecovery(output, installer.Recover(cancellationToken));
            var dependencies = new List<InstallDependency>();
            var staging = Path.Combine(Path.GetTempPath(), "ailocalpack-dev-" + Path.GetRandomFileName());
            try
            {
                if (result.GetValue(dependency) is { Length: > 0 } wanted)
                {
                    if (await PrepareDependenciesAsync(env, game, wanted, index, result.GetValue(reapply), staging, output, error, cancellationToken) is not { } prepared)
                    {
                        return 1;
                    }

                    dependencies.AddRange(prepared);
                }

                return Install(installer, game, index, new InstallRequest(game, result.GetValue(lang)!, result.GetValue(pack)!, result.GetValue(revision), files)
                {
                    Dependencies = dependencies,
                }, result.GetValue(reapply), env, output, error, cancellationToken);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, recursive: true);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A temp folder: left for the OS to clean.
                }
            }
        });
        return apply;
    }

    private static int Install(PackInstaller installer, Game game, RegistryIndex? index, InstallRequest request, bool reapply, OsEnvironment env, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        InstallResult installed;
        try
        {
            installed = reapply
                ? installer.Reapply(request, index, cancellationToken: cancellationToken)
                : installer.Install(request, index, cancellationToken: cancellationToken);
        }
        catch (GameBlockedException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }

        if (installed.Failure is { } failure)
        {
            error.WriteLine($"Couldn't install into {game.Id} \"{game.Name}\": {Explain(failure)}");
            return 1;
        }

        var record = installed.Installed!;
        var added = record.Files.Count(f => f.OriginalSha256 is null);
        output.WriteLine($"Installed {record.PackId} rev {record.Revision} ({record.Language}) into {game.Id} \"{game.Name}\": "
            + $"{Count(record.Files.Count - added, "file")} replaced, {added} added.");
        output.WriteLine($"Backup: {Path.Combine(BackupLocation.RootOf(AppPaths.Resolve(env)), RegistryLayout.GameFolder(game.Id), record.Backup)}");
        var whitelist = index?.Dependencies ?? DependencyWhitelist.Empty;
        foreach (var used in installed.Dependencies)
        {
            var name = whitelist.Find(used.Id)?.Name ?? used.Id;
            output.WriteLine(installed.SharedDependencies.Contains(used.Id)
                ? $"Dependency {name} {used.Version}: already installed, shared (used by {string.Join(", ", used.UsedBy)})."
                : $"Dependency {name} {used.Version}: {Count(used.Files.Count, "file")} installed.");
        }

        return 0;
    }

    /// <summary>Looks up <c>id@version</c> in the registry's whitelist, downloads and unpacks each for the game; <c>null</c> after printing why not.</summary>
    private static async Task<List<InstallDependency>?> PrepareDependenciesAsync(
        OsEnvironment env, Game game, string[] wanted, RegistryIndex? index, bool reapply, string staging, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var dependencies = new List<PackDependency>();
        foreach (var text in wanted)
        {
            if (text.Split('@') is not [{ Length: > 0 } id, { Length: > 0 } version])
            {
                error.WriteLine($"'{text}' is not <id>@<version>.");
                return null;
            }

            dependencies.Add(new PackDependency(id, version));
        }

        var whitelist = index?.Dependencies ?? DependencyWhitelist.Empty;
        var paths = AppPaths.Resolve(env);
        using var http = PackFetcher.CreateHttpClient(new System.Net.Http.Headers.ProductInfoHeaderValue("ailocalpack-cli", "0"));
        var preparer = new DependencyPreparer(new DependencyStore(paths, http), new EngineDetector());
        var resolution = preparer.Resolve(game, dependencies, whitelist, reapply ? null : new InstalledPackStore(paths).DependenciesOf(game.Id));
        switch (resolution.Problem)
        {
            case DependencyProblem.Unknown:
                error.WriteLine($"{resolution.Failed!.Id} {resolution.Failed.Version} is not on the registry's dependency whitelist"
                    + (whitelist.Dependencies.Count > 0 ? $" ({string.Join(", ", whitelist.Dependencies.SelectMany(d => d.Versions.Select(v => $"{d.Id}@{v.Version}")))})." : " (empty)."));
                return null;
            case DependencyProblem.PlatformUnknown:
                error.WriteLine($"Can't tell which system the build of {game.Id} \"{game.Name}\" is for: no readable main executable.");
                return null;
            case DependencyProblem.NoAsset:
                error.WriteLine($"{resolution.Failed!.Id} {resolution.Failed.Version} has no release file for {resolution.Platform}.");
                return null;
        }

        foreach (var shared in resolution.Shared)
        {
            output.WriteLine($"Dependency {shared.Dependency.Name} {shared.Version.Version}: already installed for the game; shared.");
        }

        var prepared = await preparer.PrepareAsync(resolution.Choices, staging, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var done in prepared.Prepared)
        {
            var asset = done.Choice.Asset;
            output.WriteLine(done.Fetch.FromCache
                ? $"Dependency {done.Choice.Dependency.Name} {done.Choice.Version.Version} ({asset.Platform}): from the cache."
                : $"Dependency {done.Choice.Dependency.Name} {done.Choice.Version.Version} ({asset.Platform}): downloaded {asset.Size:N0} bytes from {asset.Url.Host}, hash checked.");
        }

        if (!prepared.Succeeded)
        {
            var failed = prepared.Failed!;
            error.WriteLine($"Couldn't get {failed.Dependency.Name} {failed.Version.Version} from {failed.Asset.Url}: "
                + (prepared.Problem == DependencyProblem.Archive ? "the archive can't be unpacked safely" : $"{prepared.Fetch?.Problem}")
                + (prepared.Detail is { Length: > 0 } detail ? $" ({detail})." : "."));
            return null;
        }

        return [.. resolution.Shared.Select(c => new InstallDependency(c.Dependency.Id, c.Version.Version, [])), .. prepared.Prepared.Select(p => p.Install)];
    }

    /// <summary><c>dev fixture rpgmaker &lt;dir&gt; [--mv]</c>: a synthetic game for checking the toolkit by hand.</summary>
    private static Command BuildFixture()
    {
        var dirArgument = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var mv = new Option<bool>("--mv") { Description = "RPG Maker MV layout (www/) instead of MZ." };
        var rpgMaker = new Command("rpgmaker", "Write a tiny synthetic RPG Maker MZ (or MV) game with every kind of text the toolkit reads.") { dirArgument, mv };
        rpgMaker.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(dirArgument)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            var files = RpgMakerFixture.Create(dir, result.GetValue(mv));
            result.InvocationConfiguration.Output.WriteLine(
                $"Wrote a synthetic RPG Maker {(result.GetValue(mv) ? "MV" : "MZ")} game into {dir} ({files.Count} files).");
            return 0;
        });
        var renPyDir = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var archived = new Option<bool>("--archived") { Description = "Compiled scripts packed into game/scripts.rpa instead of loose .rpyc files." };
        var renPy = new Command("renpy", "Write a tiny synthetic Ren'Py game (compiled scripts, no engine) with every kind of text the toolkit reads.") { renPyDir, archived };
        renPy.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(renPyDir)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            var files = RenPyFixture.Create(dir, result.GetValue(archived));
            result.InvocationConfiguration.Output.WriteLine(
                $"Wrote a synthetic Ren'Py game{(result.GetValue(archived) ? " (scripts in game/scripts.rpa)" : string.Empty)} into {dir} ({files.Count} files).");
            return 0;
        });
        var godotDir = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var major = new Option<int>("--major") { Description = "Godot major version: 3 (pack format 1) or 4 (pack format 2).", DefaultValueFactory = _ => 3 };
        var embedded = new Option<bool>("--embedded") { Description = "Pack embedded at the end of Game.exe instead of Game.pck." };
        var untranslated = new Option<bool>("--untranslated") { Description = "No translation resources: the scenes hold the texts." };
        var godot = new Command("godot", "Write a tiny synthetic Godot game (a pack, no engine) with every kind of text the toolkit reads.") { godotDir, major, embedded, untranslated };
        godot.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(godotDir)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            if (result.GetValue(major) is not (3 or 4) and var version)
            {
                result.InvocationConfiguration.Error.WriteLine($"--major must be 3 or 4, not {version}.");
                return 1;
            }

            var files = GodotFixture.Create(dir, (uint)result.GetValue(major), result.GetValue(embedded), !result.GetValue(untranslated));
            result.InvocationConfiguration.Output.WriteLine(
                $"Wrote a synthetic Godot {result.GetValue(major)} game{(result.GetValue(embedded) ? " (pack embedded in Game.exe)" : string.Empty)}"
                + $"{(result.GetValue(untranslated) ? " without translations" : string.Empty)} into {dir} ({files.Count} files).");
            return 0;
        });
        var unityDir = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var il2Cpp = new Option<bool>("--il2cpp") { Description = "IL2CPP layout instead of Mono." };
        var macBundle = new Option<bool>("--mac-bundle") { Description = "macOS .app bundle layout." };
        var stripped = new Option<bool>("--stripped") { Description = "Assets without embedded type trees (TextAssets only)." };
        var bundled = new Option<bool>("--bundled") { Description = "Assets inside a UnityFS bundle." };
        var unity = new Command("unity", "Write a synthetic Unity game (serialized assets, no engine).") { unityDir, il2Cpp, macBundle, stripped, bundled };
        unity.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(unityDir)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            var files = UnityFixture.Create(dir, result.GetValue(il2Cpp), result.GetValue(macBundle), !result.GetValue(stripped), bundled: result.GetValue(bundled));
            result.InvocationConfiguration.Output.WriteLine($"Wrote a synthetic Unity game into {dir} ({files.Count} files).");
            return 0;
        });
        var unrealDir = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var unrealMac = new Option<bool>("--mac-bundle") { Description = "macOS .app/Contents/UE4 layout." };
        var locresVersion = new Option<int>("--locres-version") { Description = "Locres format version (0–3).", DefaultValueFactory = _ => 3 };
        var unreal = new Command("unreal", "Write a synthetic Unreal game (pak, native English and French localization; no engine).") { unrealDir, unrealMac, locresVersion };
        unreal.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(unrealDir)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            if (result.GetValue(locresVersion) is < 0 or > 3)
            {
                result.InvocationConfiguration.Error.WriteLine("--locres-version must be 0, 1, 2 or 3.");
                return 1;
            }

            var files = UnrealFixture.Create(dir, (byte)result.GetValue(locresVersion), result.GetValue(unrealMac));
            result.InvocationConfiguration.Output.WriteLine($"Wrote a synthetic Unreal game into {dir} ({files.Count} files).");
            return 0;
        });
        var customDir = new Argument<string>("dir") { Description = "Folder to create the game in (missing or empty)." };
        var custom = new Command("custom", "Write a synthetic game in a made-up binary format no toolkit tool reads (the agent writes its own extractor, task 57).") { customDir };
        custom.SetAction(result =>
        {
            var dir = Path.GetFullPath(result.GetValue(customDir)!);
            if (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))
            {
                result.InvocationConfiguration.Error.WriteLine($"{dir} already exists and isn't an empty folder.");
                return 1;
            }

            var files = CustomFormatFixture.Create(dir);
            result.InvocationConfiguration.Output.WriteLine($"Wrote a synthetic game in a made-up format into {dir} ({files.Count} files).");
            return 0;
        });
        return new Command("fixture", "Write synthetic games for manual checks.") { rpgMaker, renPy, godot, unity, unreal, custom };
    }

    private static Command BuildUninstall(OsEnvironment env)
    {
        var gameArgument = new Argument<string>("game") { Description = "The game (steam:<app id> or fp:<fingerprint>)." };
        var uninstall = new Command("uninstall", "Uninstall the game's pack: originals restored, added files removed, backup deleted.") { gameArgument };
        uninstall.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(gameArgument)!, error) is not var (installer, game, _))
            {
                return Task.FromResult(1);
            }

            WriteRecovery(output, installer.Recover(cancellationToken));
            var removed = installer.Uninstall(game, cancellationToken: cancellationToken);
            if (removed.Failure is { } failure)
            {
                error.WriteLine($"Couldn't uninstall from {game.Id} \"{game.Name}\": {Explain(failure)}");
                return Task.FromResult(1);
            }

            var pack = removed.Uninstalled!;
            output.WriteLine($"Uninstalled {pack.PackId} rev {pack.Revision} from {game.Id} \"{game.Name}\": "
                + $"{Count(removed.Restored.Count, "file")} restored, {removed.Removed.Count} removed.");
            foreach (var kept in removed.Kept)
            {
                output.WriteLine($"  kept (changed since the install): {kept}");
            }

            foreach (var gone in removed.RemovedDependencies)
            {
                output.WriteLine($"  removed dependency {gone.Id} {gone.Version} (no other pack uses it)");
            }

            return Task.FromResult(0);
        });
        return uninstall;
    }

    private static (PackInstaller Installer, Game Game, RegistryIndex? Index)? Open(OsEnvironment env, string gameText, TextWriter error)
    {
        if (!GameId.TryParse(gameText, out var gameId))
        {
            error.WriteLine($"'{gameText}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
            return null;
        }

        var paths = AppPaths.Resolve(env);
        if (PackCommand.FindGame(env, paths, gameId) is not { } game)
        {
            error.WriteLine($"{gameId} is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'.");
            return null;
        }

        var installer = new PackInstaller(paths, new InstalledPackStore(paths), AntiCheatPolicy.Bundled, new SystemProcessList(), env);
        return (installer, game, SavedIndex(env, paths));
    }

    /// <summary>The registry flags (opt-out, allowed anti-cheat) from a local registry or the saved copy, without network.</summary>
    internal static RegistryIndex? SavedIndex(OsEnvironment env, AppPaths paths)
    {
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
        if (!RegistryLocation.TryResolve(env, settings.RegistryIndexUrl, out var location, out _))
        {
            return null;
        }

        using var http = new HttpClient();
        var client = new RegistryIndexClient(http, paths);
        return location.IsLocal ? client.FetchAsync(location).GetAwaiter().GetResult().Index : client.LoadCached(location)?.Index;
    }

    private static void WriteRecovery(TextWriter output, RecoveryResult recovery)
    {
        var what = recovery.Outcome switch
        {
            RecoveryOutcome.RolledBack => "rolled back the interrupted install",
            RecoveryOutcome.Completed => "completed the interrupted install",
            RecoveryOutcome.UninstallFinished => "finished the interrupted uninstall",
            _ => null,
        };
        if (what is not null)
        {
            output.WriteLine($"Recovery: {what} of {recovery.PackId} in {recovery.Game}.");
        }
    }

    /// <summary>The problem in one plain sentence (CLI wording; the app has its own in resources).</summary>
    internal static string Explain(InstallFailure failure)
    {
        var file = failure.File is { } f ? f : "a file";
        var sentence = failure.Problem switch
        {
            InstallProblem.GameRunning => "the game is running; close it and retry.",
            InstallProblem.FileInUse => $"{file} is in use by another program (is the game running?); close it and retry.",
            InstallProblem.DiskFull => "there isn't enough free disk space for the backup and the new files.",
            InstallProblem.PermissionDenied => $"access to {file} was denied.",
            InstallProblem.ReadOnly => $"{file} is read-only.",
            InstallProblem.GameUnavailable => "the game folder can't be reached.",
            InstallProblem.AlreadyInstalled => "a pack is already installed; uninstall it first (or use --reapply).",
            InstallProblem.NotInstalled => "no pack is installed.",
            InstallProblem.Busy => "another install is running; wait for it to finish.",
            InstallProblem.InterruptedInstall => "an earlier install was interrupted and couldn't be finished yet"
                + (failure.File is null ? "." : $" ({failure.File})."),
            InstallProblem.VerifyFailed => $"{file} didn't have the expected content after writing.",
            InstallProblem.BackupDamaged => $"the backup of {file} is missing or damaged.",
            InstallProblem.DependencyConflict => "a dependency conflicts with what is installed (another version, or a file the pack also writes).",
            _ => $"a read or write error on {file}.",
        };
        var restored = failure.FilesRestored ? string.Empty : " Not every file could be restored yet; run the command again.";
        return failure.Detail is { Length: > 0 } detail ? $"{sentence}{restored} ({detail})" : sentence + restored;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
