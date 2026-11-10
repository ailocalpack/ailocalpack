using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>The registry manifest of a local pack: new pack vs new revision, revision credits (task 60).</summary>
public sealed class PublishPayloadsTests
{
    private static readonly DateTimeOffset Now = PublishBed.Now;
    private static readonly PackAuthor Octo = new("octo", "Octo Cat");

    private static PackManifest Existing => PublishBed.LocalManifest("full") with
    {
        Title = "Full translation",
        Authors = [new PackAuthor("alice"), new PackAuthor("bob")],
        Revisions = [new PackRevision(1, Now.AddDays(-30), "alice"), new PackRevision(2, Now.AddDays(-10), "bob")],
        Created = Now.AddDays(-30),
        Updated = Now.AddDays(-10),
    };

    /// <summary>A local update (task 56) of <see cref="Existing"/>: its authors + the placeholder, its history + one.</summary>
    private static PackManifest LocalUpdate => Existing with
    {
        Authors = [.. Existing.Authors, new PackAuthor(LocalPackStore.PlaceholderAuthor)],
        Revisions = [.. Existing.Revisions, new PackRevision(3, Now.AddDays(-1), LocalPackStore.PlaceholderAuthor, "Update for build 1001: 2 new strings.")],
    };

    [Fact]
    public void A_revision_keeps_the_original_author_first()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing, Octo, Now, RegistryRepo.Default);

        // Assert
        manifest.Authors.Should().Equal(new PackAuthor("alice"), new PackAuthor("bob"), Octo);
    }

    [Fact]
    public void A_revision_adds_one_revision_by_the_player()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing, Octo, Now, RegistryRepo.Default);

        // Assert
        manifest.Revisions.Should().Equal(Existing.Revisions[0], Existing.Revisions[1], new PackRevision(3, Now, "octo"));
    }

    [Fact]
    public void A_player_already_credited_is_not_listed_twice()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing, new PackAuthor("Bob"), Now, RegistryRepo.Default);

        // Assert
        manifest.Authors.Select(a => a.GitHub).Should().Equal("alice", "bob");
    }

    [Fact]
    public void A_revision_keeps_the_packs_id_title_and_created_date()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing, Octo, Now, RegistryRepo.Default);

        // Assert
        (manifest.Id, manifest.Title, manifest.Created, manifest.Updated).Should().Be(("full", "Full translation", Existing.Created, Now));
    }

    [Fact]
    public void A_revision_takes_the_local_packs_content()
    {
        // Arrange
        var local = PublishBed.LocalManifest() with { Coverage = new PackCoverage(64, 64), Fonts = ["noto-sans-cjk"] };

        // Act
        var manifest = PublishPayloads.Revision(local, Existing, Octo, Now, RegistryRepo.Default);

        // Assert
        (manifest.Coverage, manifest.Fonts, manifest.Builds).Should().Be((local.Coverage, local.Fonts, local.Builds));
    }

    [Fact]
    public void A_revision_starts_over_at_alpha()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing with { Status = PackStatus.Verified }, Octo, Now, RegistryRepo.Default);

        // Assert
        manifest.Status.Should().Be(PackStatus.Alpha);
    }

    [Fact]
    public void A_revision_keeps_the_update_notes()
    {
        // Act
        var manifest = PublishPayloads.Revision(LocalUpdate, Existing, Octo, Now, RegistryRepo.Default);

        // Assert
        manifest.Revisions[^1].Notes.Should().Be("Update for build 1001: 2 new strings.");
    }

    [Fact]
    public void A_revision_is_never_dated_before_the_last_one()
    {
        // Act
        var manifest = PublishPayloads.Revision(PublishBed.LocalManifest(), Existing, Octo, Now.AddDays(-20), RegistryRepo.Default);

        // Assert
        manifest.Revisions[^1].Date.Should().Be(Existing.Updated);
    }

    [Fact]
    public void A_new_pack_starts_at_revision_one_by_the_player()
    {
        // Act
        var manifest = PublishPayloads.NewPack(PublishBed.LocalManifest(), Octo, "claude-code", Now, RegistryRepo.Default);

        // Assert
        manifest.Revisions.Should().Equal(new PackRevision(1, Now, "octo"));
    }

    [Fact]
    public void A_new_pack_made_from_someone_elses_credits_them_after_the_player()
    {
        // Act
        var manifest = PublishPayloads.NewPack(LocalUpdate, Octo, "mine", Now, RegistryRepo.Default);

        // Assert
        manifest.Authors.Select(a => a.GitHub).Should().Equal("octo", "alice", "bob");
    }

    [Fact]
    public void A_new_pack_is_dated_now_in_whole_seconds()
    {
        // Act
        var manifest = PublishPayloads.NewPack(PublishBed.LocalManifest(), Octo, "claude-code", Now.AddMilliseconds(700), RegistryRepo.Default);

        // Assert
        (manifest.Created, manifest.Updated).Should().Be((Now, Now));
    }

    [Fact]
    public void A_patch_url_names_the_registrys_asset_for_the_published_revision()
    {
        // Arrange
        var local = PublishBed.LocalManifest() with
        {
            Recipe = null,
            Patch = new PackPatch("vcdiff", new Uri("https://example.com/x.zip"), new string('a', 64), 10, new Dictionary<string, string>()),
        };

        // Act
        var manifest = PublishPayloads.Revision(local, Existing, Octo, Now, new RegistryRepo("tester", "registry-test"));

        // Assert
        manifest.Patch!.Url.Should().Be(new Uri("https://github.com/tester/registry-test/releases/download/patches/steam-620-ru-full-3.zip"));
    }

    [Theory]
    [InlineData(new string[0], "claude-code")]
    [InlineData(new[] { "claude-code" }, "claude-code-2")]
    [InlineData(new[] { "claude-code", "claude-code-2" }, "claude-code-3")]
    public void A_taken_id_gets_a_number(string[] taken, string expected)
    {
        // Act
        var id = PublishPayloads.FreeId("claude-code", taken);

        // Assert
        id.Should().Be(expected);
    }

    [Fact]
    public void A_free_id_stays_within_64_characters()
    {
        // Arrange
        var preferred = new string('a', 64);

        // Act
        var id = PublishPayloads.FreeId(preferred, [preferred]);

        // Assert
        id.Should().Be(new string('a', 62) + "-2");
    }

    [Fact]
    public void The_branch_names_the_pack_and_revision()
    {
        // Arrange
        var payload = new PublishPayload(PublishBed.LocalManifest() with { Language = "pt-BR" }, null, [], null);

        // Act
        var branch = payload.Branch;

        // Assert
        branch.Should().Be("pack/steam-620/pt-BR/claude-code/rev-1");
    }
}
