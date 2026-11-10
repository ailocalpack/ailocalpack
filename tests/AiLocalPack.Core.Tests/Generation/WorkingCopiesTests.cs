using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Generation;

public sealed class WorkingCopiesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public WorkingCopiesTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _dir.File("game/Game.exe", "MZ program")
            .File("game/www/data/Map001.json", "{\"events\":[]}")
            .File("game/www/data/System.json", "{\"gameTitle\":\"Quest\"}")
            .File("game/www/audio/bgm/theme.ogg", new byte[4096])
            .File("game/movies/intro.webm", new byte[2048])
            .Dir("game/www/save");
    }

    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public static bool HostIsNotMac => !OperatingSystem.IsMacOS();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private string JobFolder => Copies().JobFolder(GameId.Steam(620), "job1");

    private WorkingCopies Copies(IFileCloner? cloner = null, long? free = null) =>
        new(_paths, cloner ?? new NoClones(), new FixedDiskSpace(free));

    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(folder, f).Replace('\\', '/'),
                f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void A_plain_copy_has_the_same_files_and_hashes_as_the_game()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var result = copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        Hashes(result.Folder).Should().BeEquivalentTo(Hashes(Game));
    }

    [Fact]
    public void Empty_folders_are_copied_too()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var result = copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        Directory.Exists(Path.Combine(result.Folder, "www", "save")).Should().BeTrue();
    }

    [Fact]
    public void The_copy_goes_into_the_game_subfolder_of_the_job_folder()
    {
        // Arrange
        var copies = Copies();

        // Act
        var folder = WorkingCopies.CopyFolder(copies.JobFolder(GameId.Steam(620), "job1"));

        // Assert
        folder.Should().Be(Path.Combine(_paths.Data, "work", "steam-620", "job1", "game"));
    }

    [Fact]
    public void Without_media_leaves_out_movies_and_audio_and_counts_them()
    {
        // Arrange
        var copies = Copies();

        // Act
        var plan = copies.Plan(Game, WorkingCopyScope.WithoutMedia, Ct);

        // Assert
        plan.Should().BeEquivalentTo(new
        {
            SkippedFiles = 2,
            SkippedBytes = 6144L,
            TotalBytes = 10L + 13 + 21,
        });
    }

    [Fact]
    public void Cloned_files_are_counted_as_cloned_not_copied()
    {
        // Arrange
        var cloner = new CopyingCloner();
        var copies = Copies(cloner);
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var result = copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        result.Should().BeEquivalentTo(new { ClonedBytes = plan.TotalBytes, CopiedBytes = 0L, Files = plan.FileCount });
    }

    [Fact]
    public void A_cloneable_copy_needs_only_the_reserve_and_the_margin()
    {
        // Arrange
        var copies = Copies(new CopyingCloner(), free: 1L << 40);
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var space = copies.CheckSpace(plan, reserveBytes: 1000);

        // Assert
        space.Should().BeEquivalentTo(new { CanClone = true, NeededBytes = 1000 + WorkingCopies.Margin, IsEnough = true });
    }

    [Fact]
    public void Probing_for_clones_leaves_nothing_behind()
    {
        // Arrange
        var copies = Copies(new CopyingCloner());
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        copies.CheckSpace(plan);

        // Assert
        Directory.EnumerateFileSystemEntries(_paths.WorkingCopies).Should().BeEmpty();
    }

    [Fact]
    public void Without_clones_the_copy_needs_its_whole_size()
    {
        // Arrange
        var copies = Copies(free: 1L << 40);
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var space = copies.CheckSpace(plan, reserveBytes: 1000);

        // Assert
        space.Should().BeEquivalentTo(new { CanClone = false, NeededBytes = plan.TotalBytes + 1000 + WorkingCopies.Margin });
    }

    [Fact]
    public void Not_enough_space_is_a_typed_error_before_anything_is_written()
    {
        // Arrange
        var copies = Copies(free: 1000);
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var act = () => copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        act.Should().Throw<WorkingCopyException>()
            .Which.Should().BeEquivalentTo(new { Problem = WorkingCopyProblem.DiskFull, Space = new { FreeBytes = 1000L } });
        Directory.Exists(JobFolder).Should().BeFalse();
    }

    [Fact]
    public void Cancelling_mid_copy_removes_the_copy_and_the_job_folder()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress<WorkingCopyProgress>(p =>
        {
            if (p.FilesDone == 2)
            {
                cancel.Cancel();
            }
        });

        // Act
        var act = () => copies.Create(plan, JobFolder, progress: progress, cancellationToken: cancel.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
        Directory.Exists(Path.Combine(_paths.WorkingCopies, "steam-620")).Should().BeFalse();
    }

    [Fact]
    public void Cancelling_keeps_a_job_folder_that_existed_before()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);
        Directory.CreateDirectory(JobFolder);
        File.WriteAllText(Path.Combine(JobFolder, "job.json"), "{}");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        // Act
        var act = () => copies.Create(plan, JobFolder, cancellationToken: cancel.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(JobFolder).Select(Path.GetFileName).Should().Equal("job.json");
    }

    [Fact]
    public void Progress_reaches_every_byte_and_file()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);
        var progress = new RecordingProgress<WorkingCopyProgress>();

        // Act
        copies.Create(plan, JobFolder, progress: progress, cancellationToken: Ct);

        // Assert
        progress.Reports[^1].Should().Be(new WorkingCopyProgress(plan.TotalBytes, plan.TotalBytes, plan.FileCount, plan.FileCount));
    }

    [Fact]
    public void A_job_that_has_a_copy_already_is_refused()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);
        copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Act
        var act = () => copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        act.Should().Throw<WorkingCopyException>().Which.Problem.Should().Be(WorkingCopyProblem.AlreadyExists);
    }

    [Fact]
    public void A_missing_game_folder_is_refused()
    {
        // Arrange
        var copies = Copies();

        // Act
        var act = () => copies.Plan(_dir.Combine("nothing"), WorkingCopyScope.AllFiles);

        // Assert
        act.Should().Throw<WorkingCopyException>().Which.Problem.Should().Be(WorkingCopyProblem.GameMissing);
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Unix permissions")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_game_file_fails_and_cleans_up()
    {
        // Arrange
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);
        var locked = _dir.Combine("game/www/data/System.json");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // Act
            var act = () => copies.Create(plan, JobFolder, cancellationToken: Ct);

            // Assert
            act.Should().Throw<WorkingCopyException>().Which.Problem.Should().Be(WorkingCopyProblem.SourceUnreadable);
            Directory.Exists(JobFolder).Should().BeFalse();
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Symbolic links need a privilege on Windows")]
    public void Links_are_recreated_not_followed()
    {
        // Arrange
        _dir.File("outside/secret.txt", "secret");
        Directory.CreateSymbolicLink(_dir.Combine("game/shared"), _dir.Combine("outside"));
        var copies = Copies();
        var plan = copies.Plan(Game, WorkingCopyScope.AllFiles, Ct);

        // Act
        var result = copies.Create(plan, JobFolder, cancellationToken: Ct);

        // Assert
        new DirectoryInfo(Path.Combine(result.Folder, "shared")).LinkTarget.Should().Be(_dir.Combine("outside"));
        plan.Entries.Should().NotContain(e => e.RelativePath.StartsWith("shared/", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_removes_the_job_folder_with_read_only_files_and_its_empty_game_folder()
    {
        // Arrange
        var copies = Copies();
        var result = copies.Create(copies.Plan(Game, WorkingCopyScope.AllFiles, Ct), JobFolder, cancellationToken: Ct);
        File.SetAttributes(Path.Combine(result.Folder, "Game.exe"), FileAttributes.ReadOnly);

        // Act
        copies.Delete(JobFolder);

        // Assert
        Directory.Exists(Path.Combine(_paths.WorkingCopies, "steam-620")).Should().BeFalse();
    }

    [Fact]
    public void Delete_refuses_a_folder_outside_the_working_copies()
    {
        // Arrange
        var copies = Copies();

        // Act
        var act = () => copies.Delete(Game);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:x")]
    public void A_job_ID_with_path_parts_is_refused(string jobId)
    {
        // Arrange
        var copies = Copies();

        // Act
        var act = () => copies.JobFolder(GameId.Steam(620), jobId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void New_job_IDs_start_with_the_UTC_time()
    {
        // Arrange
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 8, 13, 5, 9, TimeSpan.Zero));

        // Act
        var id = WorkingCopies.NewJobId(clock);

        // Assert
        id.Should().MatchRegex("^20261108-130509-[0-9a-f]{6}$");
    }

    [Fact(SkipWhen = nameof(HostIsNotMac), Skip = "APFS clonefile")]
    public void The_system_cloner_clones_on_APFS()
    {
        // Arrange
        var target = _dir.Combine("clone.json");

        // Act
        var cloned = FileCloner.Create().TryClone(_dir.Combine("game/www/data/System.json"), target);

        // Assert
        new { cloned, text = File.ReadAllText(target) }.Should().BeEquivalentTo(new { cloned = true, text = "{\"gameTitle\":\"Quest\"}" });
    }

    [Fact]
    public void The_system_cloner_never_overwrites_an_existing_file()
    {
        // Arrange
        var target = _dir.File("taken.txt", "mine").Combine("taken.txt");

        // Act
        var cloned = FileCloner.Create().TryClone(_dir.Combine("game/Game.exe"), target);

        // Assert
        new { cloned, text = File.ReadAllText(target) }.Should().BeEquivalentTo(new { cloned = false, text = "mine" });
    }

    /// <summary>Stands for a file system with clones: copies, so the content is right.</summary>
    private sealed class CopyingCloner : IFileCloner
    {
        public bool TryClone(string source, string destination)
        {
            File.Copy(source, destination);
            return true;
        }
    }
}

/// <summary>A file system without clones: every file is copied.</summary>
internal sealed class NoClones : IFileCloner
{
    public bool TryClone(string source, string destination) => false;
}

/// <summary>Free space as given (<c>null</c> = unknown).</summary>
internal sealed class FixedDiskSpace(long? free) : IDiskSpace
{
    public long? FreeBytes(string path) => free;
}
