using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>storage</c> (task 66, what Settings → Storage shows). <c>storage show [--json]</c>: the backups (folder, size,
/// games), each generation working copy (job, state, size) and the cache. <c>storage move-backups &lt;folder&gt;</c>:
/// moves the backups into an <c>ailocalpack-backups</c> folder inside it as one transaction (exit 1 when refused or
/// failed: nothing moved). <c>storage clear-cache</c>: deletes the downloads in the cache (exit 1 while an install runs).
/// </summary>
internal static class StorageCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("storage", "Disk the app uses: backups, generation working copies, cache.")
        {
            BuildShow(env),
            BuildMoveBackups(env),
            BuildClearCache(env),
        };
    }

    private static Command BuildShow(OsEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var show = new Command("show", "Show the size of the backups, each generation working copy and the cache.") { json };
        show.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var usage = StorageUsage.Measure(AppPaths.Resolve(env), cancellationToken);
            if (result.GetValue(json))
            {
                output.WriteLine(JsonSerializer.Serialize(
                    new StorageJson(
                        new BackupsJson(usage.BackupsFolder, usage.BackupBytes, usage.BackupGames),
                        [.. usage.WorkingCopies.Select(w => new WorkingCopyJson(w.Folder, w.Job?.Game.ToString(), w.Job?.GameName, w.JobId, State(w), w.Bytes))],
                        usage.CacheBytes,
                        usage.TotalBytes),
                    StorageJsonContext.Default.StorageJson));
                return Task.FromResult(0);
            }

            output.WriteLine($"backups: {GenCommand.Size(usage.BackupBytes)} for {usage.BackupGames} game(s) in {usage.BackupsFolder}");
            output.WriteLine($"working copies: {GenCommand.Size(usage.WorkingCopyBytes)}");
            foreach (var copy in usage.WorkingCopies)
            {
                output.WriteLine($"  {copy.Job?.GameName ?? copy.GameFolder} ({copy.JobId}, {State(copy)}): {GenCommand.Size(copy.Bytes)}  {copy.Folder}");
            }

            output.WriteLine($"cache: {GenCommand.Size(usage.CacheBytes)}");
            output.WriteLine($"total: {GenCommand.Size(usage.TotalBytes)}");
            return Task.FromResult(0);
        });
        return show;
    }

    private static Command BuildMoveBackups(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = $"Where to keep the backups (they go into a {BackupLocation.FolderName} folder inside it)." };
        var move = new Command("move-backups", "Move the backups of installed packs to another folder (one transaction: nothing moved if it fails).") { folder };
        move.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var location = new BackupLocation(AppPaths.Resolve(env));
            var from = location.Root;
            var moved = location.Move(result.GetValue(folder)!, cancellationToken: cancellationToken);
            if (!moved.Succeeded)
            {
                error.WriteLine($"Backups not moved ({Problem(moved.Problem!.Value)}){(moved.Detail is { } detail ? ": " + detail : ".")} They stay in {from}.");
                return Task.FromResult(1);
            }

            output.WriteLine($"Backups moved from {from} to {moved.Root}.");
            if (moved.LeftBehind is { } old)
            {
                output.WriteLine($"The old folder {old} couldn't be deleted yet; it is deleted on the next install or app start.");
            }

            return Task.FromResult(0);
        });
        return move;
    }

    private static Command BuildClearCache(OsEnvironment env)
    {
        var clear = new Command("clear-cache", "Delete the downloads in the cache (saved registry index, packs, fonts, dependencies, covers); they are downloaded again when needed.");
        clear.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var cleared = new AppCache(AppPaths.Resolve(env)).Clear();
            if (cleared.Busy)
            {
                error.WriteLine("An install, uninstall or backup move is running; try again when it has finished.");
                return Task.FromResult(1);
            }

            output.WriteLine($"Cache cleared: {GenCommand.Size(cleared.FreedBytes)} freed"
                + (cleared.LeftBytes > 0 ? $", {GenCommand.Size(cleared.LeftBytes)} in use left." : "."));
            return Task.FromResult(0);
        });
        return clear;
    }

    private static string State(WorkingCopyUsage copy) => copy.Job is { } job ? JsonNamingPolicy.KebabCaseLower.ConvertName(job.Status.ToString()) : "no-job";

    private static string Problem(BackupMoveProblem problem) => problem switch
    {
        BackupMoveProblem.Busy => "an install, uninstall or cache clearing is running",
        BackupMoveProblem.Interrupted => "an interrupted install or move has to be finished first",
        BackupMoveProblem.SameFolder => "they are already there",
        BackupMoveProblem.Nested => "the folders are inside one another",
        BackupMoveProblem.NotEmpty => "the folder already holds files",
        BackupMoveProblem.DiskFull => "not enough free space there",
        BackupMoveProblem.Denied => "the folder can't be written",
        BackupMoveProblem.VerifyFailed => "a copied file didn't read back the same",
        BackupMoveProblem.NewerFormat => "a newer app set the location; update the app",
        _ => "disk error",
    };
}

internal sealed record StorageJson(BackupsJson Backups, IReadOnlyList<WorkingCopyJson> WorkingCopies, long CacheBytes, long TotalBytes);

internal sealed record BackupsJson(string Folder, long Bytes, int Games);

internal sealed record WorkingCopyJson(string Folder, string? Game, string? GameName, string Job, string State, long Bytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(StorageJson))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
