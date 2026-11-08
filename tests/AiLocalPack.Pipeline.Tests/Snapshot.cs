using System.Runtime.CompilerServices;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>
/// Golden files in <c>Snapshots/</c>: <see cref="Text"/> gives the stored text to compare with. With
/// <c>AILOCALPACK_UPDATE_SNAPSHOTS=1</c> the actual text is written into the source folder instead (review the diff).
/// </summary>
internal static class Snapshot
{
    public static string Text(string name, string actual, [CallerFilePath] string caller = "")
    {
        var source = Path.Combine(Path.GetDirectoryName(caller)!, "Snapshots", name);
        if (Environment.GetEnvironmentVariable("AILOCALPACK_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(source, actual + "\n");
            return actual;
        }

        var stored = Path.Combine(AppContext.BaseDirectory, "Snapshots", name);
        return File.Exists(stored)
            ? File.ReadAllText(stored).ReplaceLineEndings("\n").TrimEnd('\n')
            : $"(no snapshot {name}: run the tests with AILOCALPACK_UPDATE_SNAPSHOTS=1 and review it)";
    }
}
