using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>Stage Build for strings the agent extracted: its own rebuild, then a binary patch pack (task 57).</summary>
public sealed class PatchBuildTests : IDisposable
{
    private static readonly Game Game = StageHarness.Game with { Version = GameVersion.FromBuildId("1000") };

    private readonly StageHarness _h = new();
    private readonly FakeHttpHandler _http = new();
    private readonly LocalPackStore _store;
    private readonly FakeTestRunner _runner = new(TestRunResult.Skipped(TestRunSkip.OtherPlatform, "LanternTale.exe is a Windows program"));

    public PatchBuildTests()
    {
        CustomFormatFixture.Create(_h.Copy);
        _store = new LocalPackStore(AppPaths.UnderHome(_h.Dir.Combine("home")));
    }

    public void Dispose()
    {
        _http.Dispose();
        _h.Dispose();
    }

    private static string Key(string file, ushort id) => $"{file}:{id}";

    /// <summary>Every text of the fixture as the agent's extractor would list it.</summary>
    private static string FixtureStrings() => JsonSerializer.Serialize(new
    {
        strings = CustomFormatFixture.Texts.Select(t => new { key = Key("data/text.ltx", t.Id), original = t.Text, file = "data/text.ltx" })
            .Concat(CustomFormatFixture.Names.Select(t => new { key = Key("data/names.ltx", t.Id), original = t.Text, file = "data/names.ltx" })),
    });

    /// <summary>Extracted by the agent, every text of data/text.ltx translated.</summary>
    private async Task ArrangeTranslatedAsync()
    {
        await new ExtractStage(RecipeTools.Builtin, agent: new PipelineAgent(UnknownEngineExtractTests.Extracting(FixtureStrings()), StageHarness.Server), gameName: "Lantern Tale")
            .RunStepAsync(_h.Context(GenerationStage.Extract), TestContext.Current.CancellationToken);
        _h.Files.WriteStrings(new StringTable("ru", CustomFormatFixture.Texts.ToDictionary(t => Key("data/text.ltx", t.Id), t => "Перевод " + t.Text)));
    }

    /// <summary>A rebuild that does what a correct script would: data/text.ltx again with the translations.</summary>
    private static ScriptedAgent Rebuilding(Action<string>? extra = null) => new((run, _, _) =>
    {
        var workspace = run.Request!.WorkingDirectory;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace, "translations.json")));
        var strings = json.RootElement.GetProperty("strings");
        var texts = CustomFormatFixture.Texts.Select(t => (t.Id, t.Kind, strings.TryGetProperty(Key("data/text.ltx", t.Id), out var tr) ? tr.GetString()! : t.Text));
        var output = Path.Combine(workspace, "out", "data");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "text.ltx"), CustomFormatFixture.Encode(texts));
        extra?.Invoke(workspace);
        return Task.FromResult<IReadOnlyList<AgentEvent>>([ScriptedAgent.Done]);
    });

    private Task<GenerationStepResult> BuildAsync(ScriptedAgent agent) =>
        new BuildStage(
            Game,
            new PipelineAgent(agent, StageHarness.Server),
            RecipeTools.Builtin,
            new BuildServices(new FontFixer(FontWhitelist.Bundled, new FontStore(_h.Dir.Combine("cache"), _http.CreateClient())), _store, _runner))
            .RunStepAsync(_h.Context(GenerationStage.Build, counters: new GenerationCounters(14, 12, 1, 1)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_pack_is_a_binary_patch_without_a_recipe()
    {
        // Arrange
        await ArrangeTranslatedAsync();

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        var manifest = _store.List().Single().Manifest;
        (manifest.Recipe, manifest.Patch!.Files.Keys.ToList(), manifest.Builds.Single().Files.Keys.ToList())
            .Should().BeEquivalentTo(((PackRecipe?)null, new List<string> { "data/text.ltx" }, new List<string> { "data/text.ltx" }));
    }

    [Fact]
    public async Task The_saved_patch_pack_passes_the_registry_checks()
    {
        // Arrange
        await ArrangeTranslatedAsync();

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        PackValidator.Validate(_store.List().Single().Folder, TestContext.Current.CancellationToken).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task The_pack_folder_holds_no_agent_script()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        Directory.CreateDirectory(_h.Files.AgentScriptsFolder);
        File.WriteAllText(Path.Combine(_h.Files.AgentScriptsFolder, "rebuild.py"), "print('hi')");

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        var folder = _store.List().Single().Folder;
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Should().BeEquivalentTo(["manifest.json", "strings/strings.json.gz"]);
    }

    [Fact]
    public async Task The_agents_scripts_stay_in_the_job_folder()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        Directory.CreateDirectory(_h.Files.AgentScriptsFolder);
        File.WriteAllText(Path.Combine(_h.Files.AgentScriptsFolder, "rebuild.py"), "print('hi')");

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        File.Exists(Path.Combine(_h.Files.AgentScriptsFolder, "rebuild.py")).Should().BeTrue();
    }

    [Fact]
    public async Task The_archive_applied_to_the_game_gives_the_translated_file()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        await BuildAsync(Rebuilding());
        var pack = _store.List().Single();
        var output = _h.Dir.Combine("applied");

        // Act
        var result = new PatchApplier(new FileHashCache()).Apply(pack.Manifest, _store.PatchArchivePath(pack.Manifest)!, _h.Copy, output, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Failure.Should().BeNull();
        CustomFormatFixture.Decode(File.ReadAllBytes(Path.Combine(output, "data", "text.ltx")))[4].Text.Should().Be("Перевод The lantern flickers. Someone is waiting at the old bridge.");
    }

    [Fact]
    public async Task The_archive_is_named_like_its_release_asset()
    {
        // Arrange
        await ArrangeTranslatedAsync();

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        var manifest = _store.List().Single().Manifest;
        (manifest.Patch!.Url.ToString(), File.Exists(_store.PatchArchivePath(manifest)))
            .Should().Be(("https://github.com/ailocalpack/registry/releases/download/patches/steam-620-ru-scripted-1.zip", true));
    }

    [Fact]
    public async Task The_rebuild_gets_the_accepted_translations()
    {
        // Arrange
        await ArrangeTranslatedAsync();

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        using var json = JsonDocument.Parse(File.ReadAllText(_h.Files.AgentTranslationsPath));
        (json.RootElement.GetProperty("language").GetString(), json.RootElement.GetProperty("strings").EnumerateObject().Count()).Should().Be(("ru", 12));
    }

    [Fact]
    public async Task The_rebuild_runs_in_the_workspace_with_commands()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var agent = Rebuilding();

        // Act
        await BuildAsync(agent);

        // Assert
        var request = agent.Runs.Single().Request!;
        (request.WorkingDirectory, request.BuiltinTools.Contains(AgentBuiltinTool.RunCommands)).Should().Be((_h.Files.AgentFolder, true));
    }

    [Fact]
    public async Task The_test_run_lays_the_changed_files_over_the_copy()
    {
        // Arrange
        await ArrangeTranslatedAsync();

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        var request = _runner.Requests.Single();
        (request.BuildFiles.ToList(), File.Exists(Path.Combine(request.BuildFolder, "data", "text.ltx"))).Should().BeEquivalentTo((new List<string> { "data/text.ltx" }, true));
    }

    [Fact]
    public async Task A_file_the_rebuild_left_as_it_was_is_not_patched()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var agent = Rebuilding(workspace => File.Copy(Path.Combine(_h.Copy, "data", "names.ltx"), Path.Combine(workspace, "out", "data", "names.ltx")));

        // Act
        await BuildAsync(agent);

        // Assert
        _store.List().Single().Manifest.Patch!.Files.Keys.Should().Equal("data/text.ltx");
    }

    [Fact]
    public async Task A_file_the_game_does_not_have_fails_the_stage()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var agent = Rebuilding(workspace => File.WriteAllText(Path.Combine(workspace, "out", "new.dat"), "x"));

        // Act
        var result = await BuildAsync(agent);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Should().Match<GenerationStepResult.Failed>(
            f => f.Kind == GenerationFailureKind.Agent && f.Message.Contains("out/new.dat, which isn't a file of the game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_in_the_output_fails_the_stage()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var agent = Rebuilding(workspace => File.CreateSymbolicLink(Path.Combine(workspace, "out", "config.ini"), Path.Combine(_h.Copy, "config.ini")));

        // Act
        var result = await BuildAsync(agent);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().Contain("wrote a link, out/config.ini");
    }

    [Fact]
    public async Task A_rebuild_that_changed_nothing_fails_the_stage()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var agent = new ScriptedAgent((run, _, _) =>
        {
            var output = Path.Combine(run.Request!.WorkingDirectory, "out", "data");
            Directory.CreateDirectory(output);
            File.Copy(Path.Combine(_h.Copy, "data", "text.ltx"), Path.Combine(output, "text.ltx"));
            return Task.FromResult<IReadOnlyList<AgentEvent>>([ScriptedAgent.Done]);
        });

        // Act
        var result = await BuildAsync(agent);

        // Assert
        result.Should().BeOfType<GenerationStepResult.Failed>().Which.Message.Should().Be("The agent's rebuild changed no game file.");
    }

    [Fact]
    public async Task A_rebuild_run_again_starts_from_an_empty_output_folder()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        Directory.CreateDirectory(_h.Files.AgentOutputFolder);
        File.WriteAllText(Path.Combine(_h.Files.AgentOutputFolder, "stale.dat"), "left from an earlier run");

        // Act
        var result = await BuildAsync(Rebuilding());

        // Assert
        result.Should().BeOfType<GenerationStepResult.StageDone>();
    }

    [Fact]
    public async Task The_working_copy_stays_as_it_was()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        var before = File.ReadAllBytes(Path.Combine(_h.Copy, "data", "text.ltx"));

        // Act
        await BuildAsync(Rebuilding());

        // Assert
        File.ReadAllBytes(Path.Combine(_h.Copy, "data", "text.ltx")).Should().Equal(before);
    }

    [Fact]
    public async Task Deleting_the_pack_deletes_its_archive()
    {
        // Arrange
        await ArrangeTranslatedAsync();
        await BuildAsync(Rebuilding());
        var manifest = _store.List().Single().Manifest;
        var archive = _store.PatchArchivePath(manifest)!;

        // Act
        _store.Delete(manifest.Game, manifest.Language, manifest.Id);

        // Assert
        File.Exists(archive).Should().BeFalse();
    }
}
