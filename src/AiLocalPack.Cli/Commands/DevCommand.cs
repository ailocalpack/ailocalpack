using System.CommandLine;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Developer commands for checking the install transaction by hand (task 27): <c>dev apply-files &lt;game&gt; &lt;dir&gt;</c>
/// installs the files of a folder (laid out like the game folder) as a pack, <c>dev uninstall &lt;game&gt;</c> undoes it.
/// Both first finish a transaction a crash left behind. Exit code 1 = refused or failed (files restored).
/// </summary>
internal static class DevCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("dev", "Developer tools: install a folder of files into a game and undo it, the way packs are installed.")
        {
            BuildApply(env),
            BuildUninstall(env),
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
        var apply = new Command(
            "apply-files",
            "Install the files of a folder into a game as one transaction: originals backed up, new files written and checked, everything restored on failure.")
        {
            gameArgument, dirArgument, pack, lang, revision, reapply,
        };
        apply.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(gameArgument)!, error) is not var (installer, game, index))
            {
                return Task.FromResult(1);
            }

            var dir = Path.GetFullPath(result.GetValue(dirArgument)!);
            if (!Directory.Exists(dir))
            {
                error.WriteLine($"{dir} is not a folder.");
                return Task.FromResult(1);
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new InstallFile(Path.GetRelativePath(dir, f).Replace('\\', '/'), f))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                error.WriteLine($"{dir} has no files.");
                return Task.FromResult(1);
            }

            WriteRecovery(output, installer.Recover(cancellationToken));
            var request = new InstallRequest(game, result.GetValue(lang)!, result.GetValue(pack)!, result.GetValue(revision), files);
            InstallResult installed;
            try
            {
                installed = result.GetValue(reapply)
                    ? installer.Reapply(request, index, cancellationToken: cancellationToken)
                    : installer.Install(request, index, cancellationToken: cancellationToken);
            }
            catch (GameBlockedException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }

            if (installed.Failure is { } failure)
            {
                error.WriteLine($"Couldn't install into {game.Id} \"{game.Name}\": {Explain(failure)}");
                return Task.FromResult(1);
            }

            var record = installed.Installed!;
            var added = record.Files.Count(f => f.OriginalSha256 is null);
            output.WriteLine($"Installed {record.PackId} rev {record.Revision} ({record.Language}) into {game.Id} \"{game.Name}\": "
                + $"{Count(record.Files.Count - added, "file")} replaced, {added} added.");
            output.WriteLine($"Backup: {Path.Combine(AppPaths.Resolve(env).Backups, RegistryLayout.GameFolder(game.Id), record.Backup)}");
            return Task.FromResult(0);
        });
        return apply;
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
    private static RegistryIndex? SavedIndex(OsEnvironment env, AppPaths paths)
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
            _ => $"a read or write error on {file}.",
        };
        var restored = failure.FilesRestored ? string.Empty : " Not every file could be restored yet; run the command again.";
        return failure.Detail is { Length: > 0 } detail ? $"{sentence}{restored} ({detail})" : sentence + restored;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
