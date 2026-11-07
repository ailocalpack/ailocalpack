using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Registry;

/// <summary>Downloading a pack into the cache (task 26): hashes, resume, cache hits, offline.</summary>
public sealed class PackFetcherTests : IDisposable
{
    private const string IndexUrl = "https://example.com/registry/index.json";
    private const string ManifestUrl = "https://example.com/registry/games/steam-620/ru/full/manifest.json";
    private const string StringsUrl = "https://example.com/registry/games/steam-620/ru/full/strings/strings.json.gz";
    private const string PatchUrl = "https://github.com/ailocalpack/registry/releases/download/packs/steam-620-ru-full-2.zip";

    private readonly TempDir _home = new();
    private readonly FakeHttpHandler _http = new();
    private readonly AppPaths _paths;

    public PackFetcherTests() => _paths = AppPaths.UnderHome(_home.Path);

    public void Dispose() => _home.Dispose();

    private static RegistryLocation Location(string text)
    {
        RegistryLocation.TryParse(text, out var location, out _);
        return location!;
    }

    private static PackManifest FixtureManifest =>
        PackManifestSerializer.Read(File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "games", "steam-620", "ru", "full", "manifest.json"))).Value!;

    /// <summary>A gzip string table of about <paramref name="count"/> × 40 bytes (random text, so it doesn't shrink).</summary>
    private static byte[] StringTableBytes(int count = 8000, string language = "ru")
    {
        var strings = Enumerable.Range(0, count).ToDictionary(i => $"key.{i:D5}", _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        using var stream = new MemoryStream();
        StringTableSerializer.Write(new StringTable(language, strings), stream);
        return stream.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ManifestFor(byte[] strings, byte[]? patch = null)
    {
        var manifest = FixtureManifest with { Strings = new PackStringsFile(PackStringsFile.DefaultPath, Sha256(strings), strings.Length) };
        if (patch is not null)
        {
            manifest = manifest with
            {
                Recipe = null,
                Builds = [manifest.Builds[0]],
                Patch = new PackPatch(PackPatch.VcdiffFormat, new Uri(PatchUrl), Sha256(patch), patch.Length, manifest.Builds[0].Files),
            };
        }

        return PackManifestSerializer.Write(manifest);
    }

    private PackFetcher Fetcher(int attempts = 1) =>
        new(new PackManifestClient(_http.CreateClient()), _http.CreateClient(), new PackCache(_paths), options: new PackFetchOptions(TimeSpan.FromSeconds(5), attempts));

    private Task<PackFetchResult> FetchAsync(string location = IndexUrl, int attempts = 1, IProgress<PackFetchProgress>? progress = null) =>
        Fetcher(attempts).FetchAsync(Location(location), RegistryFixtures.Pack(), progress, TestContext.Current.CancellationToken);

    private void ServeStrings(byte[] strings, byte[]? patch = null)
    {
        _http.OnGet(ManifestUrl, ManifestFor(strings, patch));
        _http.OnGet(StringsUrl, strings);
    }

    /// <summary>Sends the first <paramref name="cut"/> bytes of <paramref name="body"/>, then drops the connection.</summary>
    private static HttpResponseMessage Interrupted(byte[] body, int cut) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(body, cut)) };

    /// <summary>Answers a range request with 206 and the rest of <paramref name="body"/>.</summary>
    private static HttpResponseMessage Ranged(HttpRequestMessage request, byte[] body)
    {
        var from = request.Headers.Range?.Ranges.Single().From ?? 0;
        var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body, (int)from, body.Length - (int)from),
        };
        if (from > 0)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
        }

        return response;
    }

    [Fact]
    public async Task Local_registry_pack_lands_in_the_cache()
    {
        // Arrange
        var expected = File.ReadAllBytes(Path.Combine(RegistryFixtures.Folder, "games", "steam-620", "ru", "full", "strings", "strings.json.gz"));

        // Act
        var result = await FetchAsync(RegistryFixtures.Folder);

        // Assert
        (result.Problem, result.FromCache, Sha256(File.ReadAllBytes(result.Pack!.StringsFile!))).Should().Be((PackFetchProblem.None, false, Sha256(expected)));
    }

    [Fact]
    public async Task Fetched_pack_string_table_reads()
    {
        // Arrange
        var result = await FetchAsync(RegistryFixtures.Folder);

        // Act
        var table = result.Pack!.ReadStrings();

        // Assert
        table.Value!.Language.Should().Be("ru");
    }

    [Fact]
    public async Task Https_pack_downloads_the_string_table_next_to_the_manifest()
    {
        // Arrange
        var strings = StringTableBytes();
        ServeStrings(strings);

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Downloaded, Sha256(File.ReadAllBytes(result.Pack!.StringsFile!))).Should().Be((strings.Length, Sha256(strings)));
    }

    [Fact]
    public async Task Second_fetch_is_a_cache_hit_without_downloading_the_files()
    {
        // Arrange
        ServeStrings(StringTableBytes());
        await FetchAsync();

        // Act
        var result = await FetchAsync();

        // Assert
        (result.FromCache, _http.Requests.Count(r => r.Uri.AbsoluteUri == StringsUrl)).Should().Be((true, 1));
    }

    [Fact]
    public async Task Hash_mismatch_deletes_the_file_and_says_so()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        var tampered = strings.ToArray();
        tampered[^1] ^= 0xFF;
        _http.OnGet(StringsUrl, tampered);

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, Directory.EnumerateFiles(_paths.Packs, "*", SearchOption.AllDirectories).Any()).Should().Be((PackFetchProblem.HashMismatch, false));
    }

    [Fact]
    public async Task Body_longer_than_the_manifest_size_is_refused_and_deleted()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        _http.OnGet(StringsUrl, [.. strings, 0]);

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, Directory.EnumerateFiles(_paths.Packs, "*", SearchOption.AllDirectories).Any()).Should().Be((PackFetchProblem.HashMismatch, false));
    }

    [Fact]
    public async Task Dropped_connection_keeps_the_partial_download()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        _http.On(HttpMethod.Get, StringsUrl, _ => Interrupted(strings, 50_000));

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.MayPass, Directory.EnumerateFiles(_paths.Packs, "*" + PackCache.PartSuffix, SearchOption.AllDirectories).Single().Length > 0)
            .Should().Be((PackFetchProblem.Offline, true, true));
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_a_range_request_in_the_next_fetch()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        _http.On(HttpMethod.Get, StringsUrl, _ => Interrupted(strings, 50_000));
        await FetchAsync();
        _http.On(HttpMethod.Get, StringsUrl, request => Ranged(request, strings));

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.Downloaded, _http.Requests.Last().Header("Range"), Sha256(File.ReadAllBytes(result.Pack!.StringsFile!)))
            .Should().Be((PackFetchProblem.None, strings.Length - 50_000, "bytes=50000-", Sha256(strings)));
    }

    [Fact]
    public async Task Interrupted_download_resumes_within_the_same_fetch()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        var calls = 0;
        _http.On(HttpMethod.Get, StringsUrl, request => ++calls == 1 ? Interrupted(strings, 30_000) : Ranged(request, strings));

        // Act
        var result = await FetchAsync(attempts: 2);

        // Assert
        (result.Problem, result.Downloaded).Should().Be((PackFetchProblem.None, strings.Length));
    }

    [Fact]
    public async Task Server_that_ignores_the_range_gets_the_part_replaced()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        _http.On(HttpMethod.Get, StringsUrl, _ => Interrupted(strings, 50_000));
        await FetchAsync();
        _http.OnGet(StringsUrl, strings);

        // Act
        var result = await FetchAsync();

        // Assert
        File.ReadAllBytes(result.Pack!.StringsFile!).Should().Equal(strings);
    }

    [Fact]
    public async Task Offline_fetch_uses_the_cached_revision()
    {
        // Arrange
        ServeStrings(StringTableBytes());
        var first = await FetchAsync();
        _http.On(HttpMethod.Get, ManifestUrl, _ => throw new HttpRequestException("No route to host"));

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.FromCache, result.Pack?.Folder).Should().Be((PackFetchProblem.None, true, first.Pack!.Folder));
    }

    [Fact]
    public async Task Offline_fetch_without_a_cached_copy_fails()
    {
        // Arrange
        _http.On(HttpMethod.Get, ManifestUrl, _ => throw new HttpRequestException("No route to host"));

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.IsFetched).Should().Be((PackFetchProblem.Offline, false));
    }

    [Fact]
    public async Task Offline_fetch_ignores_a_cached_copy_of_another_revision()
    {
        // Arrange
        ServeStrings(StringTableBytes());
        await FetchAsync();
        _http.On(HttpMethod.Get, ManifestUrl, _ => throw new HttpRequestException("No route to host"));
        var newer = RegistryFixtures.Pack() with { Revision = 3 };

        // Act
        var result = await Fetcher().FetchAsync(Location(IndexUrl), newer, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problem.Should().Be(PackFetchProblem.Offline);
    }

    [Fact]
    public async Task Missing_string_table_is_reported_missing()
    {
        // Arrange
        _http.OnGet(ManifestUrl, ManifestFor(StringTableBytes()));
        _http.OnGet(StringsUrl, HttpStatusCode.NotFound);

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.MayPass).Should().Be((PackFetchProblem.FileMissing, false));
    }

    [Fact]
    public async Task String_table_in_another_language_is_deleted()
    {
        // Arrange
        var strings = StringTableBytes(10, "de");
        ServeStrings(strings);

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, Directory.EnumerateFiles(_paths.Packs, "*.gz*", SearchOption.AllDirectories).Any()).Should().Be((PackFetchProblem.InvalidStrings, false));
    }

    [Fact]
    public async Task Manifest_problem_is_passed_on()
    {
        // Arrange
        _http.OnGet(ManifestUrl, "{ \"schemaVersion\": 1 }");

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, result.Errors.Count > 0).Should().Be((PackFetchProblem.InvalidManifest, true));
    }

    [Fact]
    public async Task Patch_archive_is_downloaded_from_its_release_url()
    {
        // Arrange
        var patch = RandomNumberGenerator.GetBytes(100_000);
        ServeStrings(StringTableBytes(), patch);
        _http.OnGet(PatchUrl, patch);

        // Act
        var result = await FetchAsync();

        // Assert
        (Path.GetFileName(result.Pack!.PatchFile), Sha256(File.ReadAllBytes(result.Pack.PatchFile!))).Should().Be((PackCache.PatchFileName, Sha256(patch)));
    }

    /// <summary>A copy of the fixture registry whose pack is a patch pack; returns its folder.</summary>
    private string LocalPatchRegistry(byte[] patch)
    {
        var root = _home.CopyFolder(RegistryFixtures.Folder, "registry").Combine("registry");
        var pack = Path.Combine(root, "games", "steam-620", "ru", "full");
        File.WriteAllText(Path.Combine(pack, "manifest.json"), ManifestFor(File.ReadAllBytes(Path.Combine(pack, "strings", "strings.json.gz")), patch));
        return root;
    }

    [Fact]
    public async Task Local_registry_takes_the_patch_archive_from_its_releases_folder()
    {
        // Arrange
        var patch = RandomNumberGenerator.GetBytes(10_000);
        var root = LocalPatchRegistry(patch);
        _home.File($"registry/{PackFetcher.LocalReleasesFolder}/steam-620-ru-full-2.zip", patch);

        // Act
        var result = await FetchAsync(root);

        // Assert
        (result.Problem, Sha256(File.ReadAllBytes(result.Pack!.PatchFile!)), _http.Requests.Count).Should().Be((PackFetchProblem.None, Sha256(patch), 0));
    }

    [Fact]
    public async Task Local_registry_without_the_archive_downloads_it_from_its_release_url()
    {
        // Arrange
        var patch = RandomNumberGenerator.GetBytes(10_000);
        var root = LocalPatchRegistry(patch);
        _http.OnGet(PatchUrl, patch);

        // Act
        var result = await FetchAsync(root);

        // Assert
        Sha256(File.ReadAllBytes(result.Pack!.PatchFile!)).Should().Be(Sha256(patch));
    }

    [Fact]
    public async Task Patch_with_the_wrong_hash_leaves_no_complete_entry()
    {
        // Arrange
        var patch = RandomNumberGenerator.GetBytes(100_000);
        ServeStrings(StringTableBytes(), patch);
        _http.OnGet(PatchUrl, RandomNumberGenerator.GetBytes(100_000));

        // Act
        var result = await FetchAsync();

        // Assert
        (result.Problem, Directory.EnumerateFiles(_paths.Packs, PackCache.ManifestFileName, SearchOption.AllDirectories).Any())
            .Should().Be((PackFetchProblem.HashMismatch, false));
    }

    [Fact]
    public async Task Progress_ends_at_the_total_size()
    {
        // Arrange
        var strings = StringTableBytes();
        ServeStrings(strings);
        var progress = new RecordingProgress<PackFetchProgress>();

        // Act
        await FetchAsync(progress: progress);

        // Assert
        progress.Reports[^1].Should().Be(new PackFetchProgress(strings.Length, strings.Length));
    }

    [Fact]
    public async Task Resumed_bytes_are_counted_once_in_the_progress()
    {
        // Arrange
        var strings = StringTableBytes();
        _http.OnGet(ManifestUrl, ManifestFor(strings));
        var calls = 0;
        _http.On(HttpMethod.Get, StringsUrl, request => ++calls == 1 ? Interrupted(strings, 30_000) : Ranged(request, strings));
        var progress = new RecordingProgress<PackFetchProgress>();

        // Act
        await FetchAsync(attempts: 2, progress: progress);

        // Assert
        progress.Reports.Max(r => r.Done).Should().Be(strings.Length);
    }

    /// <summary>Gives the first bytes of a body, then fails like a dropped connection.</summary>
    private sealed class BreakingStream(byte[] body, int cut) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cut)
            {
                throw new IOException("The connection was reset.");
            }

            var n = Math.Min(count, cut - _position);
            Array.Copy(body, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
