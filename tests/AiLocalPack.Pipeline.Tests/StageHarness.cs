using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Core.Background;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>A job folder with a working copy of a fixture game, and the context of one step at a stage.</summary>
internal sealed class StageHarness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public StageHarness()
    {
        Folder = Dir.Combine("work/steam-620/job");
        Directory.CreateDirectory(Folder);
    }

    public TempDir Dir { get; } = new();

    public FakeClock Clock { get; } = new(T0);

    public string Folder { get; }

    public string Copy => WorkingCopies.CopyFolder(Folder);

    public GenerationFiles Files => new(Folder);

    public string Log => File.Exists(Path.Combine(Folder, "logs", "job.log")) ? File.ReadAllText(Path.Combine(Folder, "logs", "job.log")) : "";

    public static Game Game { get; } = new(GameId.Steam(620), "Sample Quest", GameSource.Steam, "/games/sample", null, null, "rpgmaker:mz", "none", null, true);

    public static ToolkitServerProgram Server { get; } =
        ToolkitServerProgram.Cli(new AppLaunch("/bin/ailocalpack", []), new OsEnvironment(OsKind.Linux, "/home/p", _ => null));

    /// <summary>The working copy holds the RPG Maker MZ fixture.</summary>
    public StageHarness WithRpgMakerCopy()
    {
        Dir.RpgMakerGame("work/steam-620/job/game");
        return this;
    }

    public GenerationStepContext Context(GenerationStage stage, string language = "ru", GenerationOptions? options = null)
    {
        var job = GenerationJobMachine.Create("job", Game.Id, Game.Name, language, "fake", options ?? GenerationOptions.Default, T0) with { Stage = stage };
        return new GenerationStepContext(new GenerationJobEntry(Folder, job), Clock);
    }

    public void Dispose() => Dir.Dispose();
}
