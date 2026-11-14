using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>The fix conversation and drafts of a job folder (task 75).</summary>
public sealed class FixFilesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private FixFiles Files => new(_dir.Combine("job"));

    private static FixState State => new(
        [
            FixTurn.FromPlayer(1, T0, "The game crashes.", [FixAttachment.Session(TimeSpan.FromSeconds(4.2), 3), FixAttachment.Changes(), FixAttachment.ForStrings(["a", "b"])]),
            new FixTurn(2, FixTurnKind.Agent, T0.AddMinutes(3), "Fixed the separators.", [], ["search", "put_translations", "app:build"], [], FixTurnOutcome.Changed,
                Change: new FixChange(2, 3, 1, true, TestRunOutcome.Passed, [new FixStringChange("a", "Fire | Ice", "Огонь | Лёд", "Огонь и лёд")]), Model: "sonnet"),
            FixTurn.FromPlayer(3, T0.AddMinutes(10), "Use «вы».", []),
            new FixTurn(4, FixTurnKind.Agent, T0.AddMinutes(12), "Here is the plan.", [], [], [], FixTurnOutcome.Planned,
                Plan: new FixPlan(60, 0, false, [new FixStringChange("b", "Hi", "Привет", "Здравствуйте")])),
        ],
        [new FixDraft(1, T0.AddDays(-1)), new FixDraft(2, T0.AddMinutes(3), 2)],
        2);

    [Fact]
    public void A_written_state_reads_back_the_same()
    {
        // Arrange
        Files.Write(State);

        // Act
        var read = Files.Read();

        // Assert
        read.Should().BeEquivalentTo(State);
    }

    [Fact]
    public void No_state_reads_as_empty()
    {
        // Act
        var read = Files.Read();

        // Assert
        read.Should().Be(FixState.Empty);
    }

    [Fact]
    public void A_broken_state_is_moved_aside_and_reads_as_empty()
    {
        // Arrange
        Directory.CreateDirectory(Files.Folder);
        File.WriteAllText(Files.StatePath, "{ not json");

        // Act
        var read = Files.Read();

        // Assert
        (read, Directory.GetFiles(Files.Folder, "state.json.*.bak").Length).Should().Be((FixState.Empty, 1));
    }

    [Fact]
    public void A_newer_state_is_never_overwritten()
    {
        // Arrange
        Directory.CreateDirectory(Files.Folder);
        File.WriteAllText(Files.StatePath, """{"schemaVersion": 2, "current": 1}""");

        // Act
        var act = () => Files.Write(State);

        // Assert
        act.Should().Throw<IOException>();
    }

    [Fact]
    public void The_waiting_plan_is_the_last_turn_with_a_plan_not_acted_on()
    {
        // Act
        var waiting = State.WaitingPlan;

        // Assert
        waiting!.Id.Should().Be(4);
    }

    [Fact]
    public void The_next_draft_follows_the_highest_one()
    {
        // Act
        var next = State.NextDraft;

        // Assert
        next.Should().Be(3);
    }

    [Fact]
    public void A_kept_log_holds_its_lines_in_the_attachments_folder()
    {
        // Act
        var attachment = Files.AddLog("log.txt", ["one", "two"]);

        // Assert
        (File.ReadAllText(attachment.Path!), attachment.Lines, Path.GetDirectoryName(attachment.Path)).Should().Be(("one\ntwo\n", 2, Files.AttachmentsFolder));
    }

    [Fact]
    public void A_second_attachment_of_the_same_name_gets_a_free_one()
    {
        // Arrange
        Files.AddLog("log.txt", ["one"]);

        // Act
        var second = Files.AddLog("log.txt", ["two"]);

        // Assert
        Path.GetFileName(second.Path).Should().Be("log-2.txt");
    }

    [Fact]
    public void A_screenshot_is_copied_into_the_job()
    {
        // Arrange
        _dir.File("shot.png", "png");

        // Act
        var attachment = Files.AddScreenshot(_dir.Combine("shot.png"));

        // Assert
        (attachment.Kind, File.ReadAllText(attachment.Path!)).Should().Be((FixAttachmentKind.Screenshot, "png"));
    }
}

/// <summary>The part of a job a fix changes, copied aside and back (task 75).</summary>
public sealed class FixJobStateTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GenerationFiles Job => new(_dir.Combine("job"));

    private void Seed(string translation, string? script = null)
    {
        Directory.CreateDirectory(Job.Folder);
        Job.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["a"] = translation }));
        Job.WriteGlossary(new Glossary("ru", "informal", [new GlossaryEntry("Potion", "Зелье")]));
        if (script is not null)
        {
            Directory.CreateDirectory(Job.AgentScriptsFolder);
            File.WriteAllText(Path.Combine(Job.AgentScriptsFolder, "rebuild.py"), script);
        }
    }

    [Fact]
    public void A_captured_state_put_back_undoes_later_changes()
    {
        // Arrange
        Seed("Огонь | Лёд");
        FixJobState.Capture(Job, _dir.Combine("before"));
        Job.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["a"] = "Огонь и лёд" }));

        // Act
        FixJobState.Restore(_dir.Combine("before"), Job);

        // Assert
        Job.ReadStrings()!.Strings["a"].Should().Be("Огонь | Лёд");
    }

    [Fact]
    public void Putting_back_restores_the_agent_scripts()
    {
        // Arrange
        Seed("x", script: "old");
        FixJobState.Capture(Job, _dir.Combine("before"));
        File.WriteAllText(Path.Combine(Job.AgentScriptsFolder, "rebuild.py"), "new");

        // Act
        FixJobState.Restore(_dir.Combine("before"), Job);

        // Assert
        File.ReadAllText(Path.Combine(Job.AgentScriptsFolder, "rebuild.py")).Should().Be("old");
    }

    [Fact]
    public void A_changed_script_changes_the_fingerprint()
    {
        // Arrange
        Seed("x", script: "old");
        var before = FixJobState.ScriptsFingerprint(Job);
        File.WriteAllText(Path.Combine(Job.AgentScriptsFolder, "rebuild.py"), "new");

        // Act
        var after = FixJobState.ScriptsFingerprint(Job);

        // Assert
        after.Should().NotBe(before);
    }

    [Fact]
    public void A_job_without_scripts_has_no_fingerprint()
    {
        // Arrange
        Seed("x");

        // Act
        var fingerprint = FixJobState.ScriptsFingerprint(Job);

        // Assert
        fingerprint.Should().BeNull();
    }

    [Fact]
    public void Putting_back_a_missing_capture_throws()
    {
        // Act
        var act = () => FixJobState.Restore(_dir.Combine("nothing"), Job);

        // Assert
        act.Should().Throw<IOException>();
    }
}

/// <summary>When a session with an AI translation installed reads as a crash (task 75).</summary>
public sealed class GameCrashRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4, true)]
    [InlineData(29.9, true)]
    [InlineData(30, false)]
    [InlineData(600, false)]
    public void A_session_shorter_than_30_seconds_is_a_crash(double seconds, bool crash)
    {
        // Arrange
        var ended = new GameSessionEnded(GameId.Steam(620), T0, T0.AddSeconds(seconds));

        // Act
        var isCrash = GameCrashRule.IsCrash(ended);

        // Assert
        isCrash.Should().Be(crash);
    }
}

/// <summary>Folder copies for drafts (task 75).</summary>
public sealed class FolderCopyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Replace_makes_the_destination_a_copy_of_the_source()
    {
        // Arrange
        _dir.File("a/x.txt", "1");
        _dir.File("a/sub/y.txt", "2");
        _dir.File("b/old.txt", "old");

        // Act
        FolderCopy.Replace(_dir.Combine("a"), _dir.Combine("b"));

        // Assert
        Directory.GetFiles(_dir.Combine("b"), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dir.Combine("b"), f).Replace('\\', '/')).Order()
            .Should().Equal("sub/y.txt", "x.txt");
    }

    [Fact]
    public void Replacing_with_a_missing_source_deletes_the_destination()
    {
        // Arrange
        _dir.File("b/old.txt", "old");

        // Act
        FolderCopy.Replace(_dir.Combine("a"), _dir.Combine("b"));

        // Assert
        Directory.Exists(_dir.Combine("b")).Should().BeFalse();
    }

    [Fact]
    public void Folders_with_the_same_files_have_the_same_fingerprint()
    {
        // Arrange
        _dir.File("a/x.txt", "1");
        _dir.File("b/x.txt", "1");

        // Act
        var (a, b) = (FolderCopy.Fingerprint(_dir.Combine("a")), FolderCopy.Fingerprint(_dir.Combine("b")));

        // Assert
        a.Should().Be(b);
    }
}
