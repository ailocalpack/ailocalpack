using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>A local pack store, a signed-in keychain and a fake GitHub with the registry <c>ailocalpack/registry</c>.</summary>
internal sealed class PublishBed : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    public static readonly GameId Game = GameId.Steam(620);
    public static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));
    public const string Registry = "ailocalpack/registry";

    private readonly TempDir _home = new();
    private readonly HttpClient _client;

    public PublishBed()
    {
        Api = new FakeGitHubApi(App.Api).AddRepo(Registry);
        _client = new HttpClient(Api, disposeHandler: false);
        Paths = AppPaths.UnderHome(_home.Path);
        Store = new LocalPackStore(Paths);
        Keychain.WriteAsync(GitHubSession.TokenSecret, FakeGitHubApi.Token).GetAwaiter().GetResult();
    }

    public FakeGitHubApi Api { get; }

    public AppPaths Paths { get; }

    public LocalPackStore Store { get; }

    public FakeSecretStore Keychain { get; } = new();

    public FakeClock Clock { get; } = new(Now);

    public List<TimeSpan> Waits { get; } = [];

    public void Dispose()
    {
        _client.Dispose();
        Api.Dispose();
        _home.Dispose();
    }

    public PackPublisher Publisher()
    {
        var session = new GitHubSession(Keychain, new GitHubDeviceFlow(_client, App, Clock), new GitHubUserClient(_client, App), Paths, App, Clock);
        return new PackPublisher(session, _client, Store, Paths, Clock, (span, _) =>
        {
            Waits.Add(span);
            Clock.Advance(span);
            return Task.CompletedTask;
        });
    }

    public Task<PublishResult> PublishAsync(LocalPack pack, PublishTarget? target = null, IProgress<PublishStep>? progress = null) =>
        Publisher().PublishAsync(new PublishRequest(pack, target ?? new PublishTarget.NewPack(), RegistryRepo.Default, GameName: "Portal 2"), progress, TestContext.Current.CancellationToken);

    public static PackManifest LocalManifest(string id = "claude-code") => new(
        id,
        Game,
        "ru",
        "Claude Code translation",
        [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
        [new PackRevision(1, Now.AddDays(-1), LocalPackStore.PlaceholderAuthor)],
        PackStatus.Alpha,
        new PackAgent("claude-code", "2.1.319", "claude-opus-5-5"),
        [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
        new PackRecipe("rpgmaker", "1.0.0"),
        null,
        null,
        [],
        ["noto-sans"],
        new PackCoverage(2, 2),
        Now.AddDays(-1),
        Now.AddDays(-1));

    public static StringTable Table => new("ru", new Dictionary<string, string> { ["a"] = "Зелье", ["b"] = "Эфир" });

    public static LocalPackReport Report() => new(
        "job-1",
        Now.AddDays(-1),
        TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Game.exe is a Windows program"),
        new LocalPackFonts(true, "noto-sans", ["Fixture Sans"], []),
        [],
        [],
        0,
        Tried: Now.AddHours(-1));

    public LocalPack SaveLocal(PackManifest? manifest = null) => Store.Save(manifest ?? LocalManifest(), Table, Report());

    /// <summary>A registry pack <c>full</c> by alice (rev 1) in <c>games/steam-620/ru/full</c>, plus <paramref name="extra"/> files in its folder.</summary>
    public PackManifest AddRegistryPack(string id = "full", params string[] extra)
    {
        var manifest = LocalManifest(id) with
        {
            Title = "Full translation",
            Authors = [new PackAuthor("alice", "Alice")],
            Revisions = [new PackRevision(1, Now.AddDays(-30), "alice")],
            Strings = new PackStringsFile(PackStringsFile.DefaultPath, new string('b', 64), 10),
            Created = Now.AddDays(-30),
            Updated = Now.AddDays(-30),
        };
        var folder = $"games/steam-620/ru/{id}";
        var files = new Dictionary<string, string?>
        {
            [$"{folder}/manifest.json"] = PackManifestSerializer.Write(manifest),
            [$"{folder}/strings/strings.json.gz"] = "old strings",
        };
        foreach (var path in extra)
        {
            files[$"{folder}/{path}"] = "old";
        }

        Api.Commit(Registry, "master", files, $"Add {id}");
        return manifest;
    }

    public PackManifest CommittedManifest(string fork, string branch, string folder) =>
        PackManifestSerializer.Read(Encoding.UTF8.GetString(Api.Files(fork, branch)![$"{folder}/manifest.json"])).Value!;
}
