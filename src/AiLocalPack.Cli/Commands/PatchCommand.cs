using System.CommandLine;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>toolkit patch &lt;folder&gt; --pack &lt;pack folder | manifest.json&gt; --out &lt;dir&gt; [--patch &lt;zip&gt;]</c>: applies a
/// binary patch pack (task 34) to a game folder the way install does (<see cref="PatchApplier"/>: each touched file must
/// be the one the patch was made from, results checked against the manifest), writing the patched files into another
/// folder (feed it to <c>dev apply-files</c>). The archive defaults to <c>patch.zip</c> next to the manifest, as the pack
/// cache (<c>pack fetch</c>) keeps it. Exit 1 when it can't be applied.
/// </summary>
internal static class PatchCommand
{
    public static Command Build(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = "Game folder (read only)." };
        var pack = new Option<string>("--pack") { Description = "The patch pack: its folder (as 'pack fetch' prints it) or its manifest.json.", Required = true };
        var patch = new Option<string?>("--patch") { Description = "The patch archive (ZIP of <path>.vcdiff entries); default: patch.zip next to the manifest." };
        var outDir = new Option<string>("--out") { Description = "Folder for the patched files, laid out like the game folder.", Required = true };
        var command = new Command("patch", "Apply a binary patch pack to the game's own files, into another folder.")
        {
            folder, pack, patch, outDir,
        };
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var game = Path.GetFullPath(result.GetValue(folder)!);
            if (!Directory.Exists(game))
            {
                error.WriteLine($"{game}: no such folder.");
                return 1;
            }

            var packPath = Path.GetFullPath(result.GetValue(pack)!);
            if (PackCommand.ReadManifest(packPath, error) is not { } manifest)
            {
                return 1;
            }

            if (manifest.Patch is null)
            {
                error.WriteLine($"{manifest.Id} is a recipe pack, not a binary patch; rebuild it with 'toolkit rebuild'.");
                return 1;
            }

            var archive = result.GetValue(patch) is { } given
                ? Path.GetFullPath(given)
                : Path.Combine(Directory.Exists(packPath) ? packPath : Path.GetDirectoryName(packPath)!, PackCache.PatchFileName);
            if (!File.Exists(archive))
            {
                error.WriteLine($"{archive}: no patch archive (fetch the pack with 'pack fetch', or pass --patch).");
                return 1;
            }

            var target = Path.GetFullPath(result.GetValue(outDir)!);
            if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(game), StringComparison.Ordinal))
            {
                error.WriteLine("--out must not be the game folder: the patched files go elsewhere and are installed with 'dev apply-files'.");
                return 1;
            }

            var hashes = FileHashCache.Load(AppPaths.Resolve(env).FileHashes);
            var applied = new PatchApplier(hashes).Apply(manifest, archive, game, target);
            hashes.Save();
            if (applied.Failure is { } failure)
            {
                error.WriteLine(Explain(failure));
                return 1;
            }

            output.WriteLine($"Patched {applied.Files.Count} files into {target}.");
            foreach (var file in applied.Files)
            {
                output.WriteLine(applied.AlreadyPatched.Contains(file) ? $"  {file} (already patched, copied as is)" : $"  {file}");
            }

            return 0;
        });
        return command;
    }

    private static string Explain(PatchFailure failure) => failure.Problem switch
    {
        PatchProblem.SourceMissing => $"{failure.File}: not in the game folder; the patch can't be applied.",
        PatchProblem.SourceMismatch =>
            $"{failure.File}: not the file the patch was made from (sha256 {failure.ActualSha256}, the patch needs {failure.ExpectedSha256}); the pack is incompatible with this copy of the game.",
        PatchProblem.SourceUnreadable => $"{failure.File}: can't be read ({failure.Detail}).",
        PatchProblem.ResultMismatch => $"{failure.File}: the patched file has sha256 {failure.ActualSha256}, not {failure.ExpectedSha256} as the manifest says; the patch is damaged.",
        PatchProblem.Decode => $"{failure.File}: {failure.Detail}",
        PatchProblem.DiskError => $"Can't write the patched files: {failure.Detail}",
        _ => failure.Detail ?? "The patch archive is damaged.",
    };
}
