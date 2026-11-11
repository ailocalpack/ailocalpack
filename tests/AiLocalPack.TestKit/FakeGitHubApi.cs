using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiLocalPack.TestKit;

/// <summary>
/// An in-memory GitHub REST API for the publish client (task 60): repositories sharing one object store (a fork
/// network), refs, blobs / trees / commits of the Git Data API, the contents API (folder listings and raw files),
/// forks, merge-upstream, compare, Releases with assets and uploads, pull requests, <c>/user</c>. Trees are flat
/// (path → blob). <see cref="Fail"/> injects an error answer for the next matching calls. Every call is recorded.
/// Usable as an <see cref="HttpMessageHandler"/> or through <see cref="HandleAsync"/> (the CLI's loopback server).
/// </summary>
public sealed partial class FakeGitHubApi : HttpMessageHandler
{
    public const string Token = "gho_fake_token";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly Dictionary<string, Dictionary<string, string>> _trees = [];
    private readonly Dictionary<string, FakeCommit> _commits = [];
    private readonly Dictionary<string, FakeRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FakeFailure> _failures = [];
    private readonly List<(string Method, string Path, string? Body)> _calls = [];
    private int _nextId = 100;

    /// <param name="root">The API root (ends with <c>/</c>), e.g. <c>https://api.github.example/</c>.</param>
    public FakeGitHubApi(Uri root, string login = "octo")
    {
        Root = root;
        Login = login;
    }

    public Uri Root { get; }

    public string Login { get; }

    /// <summary>The name a new fork gets (GitHub adds <c>-1</c> when the player already has a repository of that name).</summary>
    public string? ForkName { get; set; }

    /// <summary>How many reads of a new fork's branch answer 404 before it is ready (GitHub makes forks in the background).</summary>
    public int ForkReadyAfter { get; set; }

    /// <summary>Calls as <c>METHOD path?query</c> (path relative to <see cref="Root"/>), in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls.Select(c => $"{c.Method} {c.Path}")];
            }
        }
    }

    /// <summary>The JSON bodies sent to <paramref name="method"/> calls whose path starts with <paramref name="pathPrefix"/>.</summary>
    public IReadOnlyList<JsonNode> Bodies(string method, string pathPrefix)
    {
        lock (_lock)
        {
            return [.. _calls.Where(c => c.Method == method && c.Path.StartsWith(pathPrefix, StringComparison.Ordinal) && c.Body is not null).Select(c => JsonNode.Parse(c.Body!)!)];
        }
    }

    /// <summary>Pull requests opened, in order.</summary>
    public IReadOnlyList<FakePullRequest> PullRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _repos.Values.SelectMany(r => r.Pulls)];
            }
        }
    }

    /// <summary>Makes a repository with one commit on <paramref name="branch"/> holding <paramref name="files"/>.</summary>
    public FakeGitHubApi AddRepo(string fullName, IReadOnlyDictionary<string, string>? files = null, string branch = "master")
    {
        lock (_lock)
        {
            var tree = (files ?? new Dictionary<string, string> { ["README.md"] = "registry" })
                .ToDictionary(f => f.Key, f => PutBlob(Encoding.UTF8.GetBytes(f.Value)), StringComparer.Ordinal);
            var commit = PutCommit(tree, [], "Initial commit");
            _repos[fullName] = new FakeRepo(fullName, branch, null) { Refs = { [branch] = commit } };
        }

        return this;
    }

    /// <summary>Makes <paramref name="fullName"/> a fork of <paramref name="parent"/> at its current head (an existing fork).</summary>
    public FakeGitHubApi AddFork(string fullName, string parent)
    {
        lock (_lock)
        {
            var source = _repos[parent];
            _repos[fullName] = new FakeRepo(fullName, source.DefaultBranch, parent) { Refs = { [source.DefaultBranch] = source.Refs[source.DefaultBranch] } };
        }

        return this;
    }

    /// <summary>Adds a commit to <paramref name="fullName"/>'s <paramref name="branch"/> with these files changed.</summary>
    public string Commit(string fullName, string branch, IReadOnlyDictionary<string, string?> changes, string message = "Change")
    {
        lock (_lock)
        {
            var repo = _repos[fullName];
            var parent = repo.Refs.GetValueOrDefault(branch) ?? repo.Refs[repo.DefaultBranch];
            var tree = new Dictionary<string, string>(_trees[_commits[parent].Tree], StringComparer.Ordinal);
            foreach (var (path, text) in changes)
            {
                if (text is null)
                {
                    tree.Remove(path);
                }
                else
                {
                    tree[path] = PutBlob(Encoding.UTF8.GetBytes(text));
                }
            }

            var sha = PutCommit(tree, [parent], message);
            repo.Refs[branch] = sha;
            return sha;
        }
    }

    /// <summary>Opens a pull request (as if made earlier) from <paramref name="head"/> (<c>owner:branch</c>).</summary>
    public FakePullRequest OpenPullRequest(string registry, string head, string title = "Earlier")
    {
        lock (_lock)
        {
            var repo = _repos[registry];
            var number = _nextId++;
            var pr = new FakePullRequest(number, title, head, repo.DefaultBranch, "", new Uri($"https://github.com/{registry}/pull/{number}"));
            repo.Pulls.Add(pr);
            return pr;
        }
    }

    /// <summary>The files at the head of <paramref name="branch"/> (path → text), or <c>null</c> when there is no such branch.</summary>
    public IReadOnlyDictionary<string, byte[]>? Files(string fullName, string branch)
    {
        lock (_lock)
        {
            return _repos.TryGetValue(fullName, out var repo) && repo.Refs.TryGetValue(branch, out var sha)
                ? _trees[_commits[sha].Tree].ToDictionary(f => f.Key, f => _blobs[f.Value], StringComparer.Ordinal)
                : null;
        }
    }

    /// <summary>The commit messages from the head of <paramref name="branch"/> back to the first.</summary>
    public IReadOnlyList<string> History(string fullName, string branch)
    {
        lock (_lock)
        {
            var list = new List<string>();
            for (var sha = _repos[fullName].Refs.GetValueOrDefault(branch); sha is not null; sha = _commits[sha].Parents.FirstOrDefault())
            {
                list.Add(_commits[sha].Message);
            }

            return list;
        }
    }

    public bool HasRepo(string fullName)
    {
        lock (_lock)
        {
            return _repos.ContainsKey(fullName);
        }
    }

    /// <summary>The assets of the repository's Release <paramref name="tag"/> (name → bytes).</summary>
    public IReadOnlyDictionary<string, byte[]> Assets(string fullName, string tag)
    {
        lock (_lock)
        {
            return _repos[fullName].Releases.FirstOrDefault(r => r.Tag == tag)?.Assets.ToDictionary(a => a.Name, a => a.Content) ?? [];
        }
    }

    /// <summary>The next <paramref name="times"/> calls of <paramref name="method"/> whose path matches <paramref name="pathPattern"/> (regex, relative to <see cref="Root"/>) answer this.</summary>
    public FakeGitHubApi Fail(string method, string pathPattern, HttpStatusCode status, string body, IReadOnlyDictionary<string, string>? headers = null, int times = 1)
    {
        lock (_lock)
        {
            _failures.Add(new FakeFailure(method, new Regex(pathPattern, RegexOptions.CultureInvariant), status, body, headers ?? new Dictionary<string, string>(), times));
        }

        return this;
    }

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var url = request.RequestUri!;
        var relative = url.AbsoluteUri.StartsWith(Root.AbsoluteUri, StringComparison.Ordinal) ? url.AbsoluteUri[Root.AbsoluteUri.Length..] : url.PathAndQuery.TrimStart('/');
        var method = request.Method.Method;
        var isUpload = relative.StartsWith("uploads/", StringComparison.Ordinal);
        lock (_lock)
        {
            _calls.Add((method, relative, body is null || isUpload ? null : Encoding.UTF8.GetString(body)));
            var failure = _failures.FirstOrDefault(f => f.Method == method && f.Pattern.IsMatch(relative.Split('?')[0]) && f.Times > 0);
            if (failure is not null)
            {
                failure.Times--;
                var failed = Reply(failure.Status, failure.Body);
                foreach (var (name, value) in failure.Headers)
                {
                    failed.Headers.TryAddWithoutValidation(name, value);
                }

                return failed;
            }

            if (request.Headers.Authorization?.Parameter != Token)
            {
                return Reply(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");
            }

            var raw = request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github.raw+json");
            return Route(method, relative, body, raw);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => HandleAsync(request, cancellationToken);

    private HttpResponseMessage Route(string method, string relative, byte[]? body, bool raw)
    {
        var parts = relative.Split('?', 2);
        var path = Uri.UnescapeDataString(parts[0]);
        var query = parts.Length > 1 ? parts[1].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : "")) : [];
        var json = body is { Length: > 0 } && !path.StartsWith("uploads/", StringComparison.Ordinal) ? JsonNode.Parse(body) : null;
        if (path == "user" && method == "GET")
        {
            return Ok(new JsonObject { ["login"] = Login, ["name"] = "Octo Cat", ["html_url"] = $"https://github.com/{Login}" });
        }

        if (UploadPattern().Match(path) is { Success: true } upload)
        {
            return UploadAsset(upload.Groups["repo"].Value, long.Parse(upload.Groups["id"].Value, CultureInfo.InvariantCulture), query["name"], body ?? []);
        }

        if (RepoPattern().Match(path) is not { Success: true } match)
        {
            return NotFound();
        }

        var name = match.Groups["repo"].Value;
        var rest = match.Groups["rest"].Value;
        if (rest == "forks" && method == "POST")
        {
            return Fork(name);
        }

        if (!_repos.TryGetValue(name, out var repo))
        {
            return NotFound();
        }

        return (method, rest) switch
        {
            ("GET", "") => Ok(RepoJson(repo)),
            ("POST", "merge-upstream") => MergeUpstream(repo),
            ("GET", _) when rest.StartsWith("git/ref/heads/", StringComparison.Ordinal) => GetRef(repo, rest["git/ref/heads/".Length..]),
            ("PATCH", _) when rest.StartsWith("git/refs/heads/", StringComparison.Ordinal) => UpdateRef(repo, rest["git/refs/heads/".Length..], json!),
            ("POST", "git/refs") => CreateRef(repo, json!),
            ("GET", _) when rest.StartsWith("git/commits/", StringComparison.Ordinal) => GetCommit(rest["git/commits/".Length..]),
            ("POST", "git/blobs") => Created(new JsonObject { ["sha"] = PutBlob(Convert.FromBase64String((string)json!["content"]!)) }),
            ("POST", "git/trees") => CreateTree(json!),
            ("POST", "git/commits") => CreateCommit(json!),
            ("GET", _) when rest.StartsWith("contents/", StringComparison.Ordinal) => Contents(repo, rest["contents/".Length..], query.GetValueOrDefault("ref"), raw),
            ("GET", "contents") => Contents(repo, "", query.GetValueOrDefault("ref"), raw),
            ("GET", _) when rest.StartsWith("releases/tags/", StringComparison.Ordinal) => repo.Releases.FirstOrDefault(r => r.Tag == rest["releases/tags/".Length..]) is { } release ? Ok(ReleaseJson(repo, release)) : NotFound(),
            ("POST", "releases") => CreateRelease(repo, json!),
            ("GET", _) when ReleaseAssetsPattern().Match(rest) is { Success: true } assets => ListAssets(repo, long.Parse(assets.Groups["id"].Value, CultureInfo.InvariantCulture), query),
            ("DELETE", _) when rest.StartsWith("releases/assets/", StringComparison.Ordinal) => DeleteAsset(repo, long.Parse(rest["releases/assets/".Length..], CultureInfo.InvariantCulture)),
            ("GET", _) when rest.StartsWith("compare/", StringComparison.Ordinal) => Compare(rest["compare/".Length..]),
            ("GET", "pulls") => Ok(new JsonArray([.. repo.Pulls.Where(p => p.State == "open" && (!query.TryGetValue("head", out var head) || p.Head == head)).Select(PullJson)])),
            ("POST", "pulls") => CreatePull(repo, json!),
            _ => NotFound(),
        };
    }

    private HttpResponseMessage Fork(string parentName)
    {
        if (!_repos.TryGetValue(parentName, out var parent))
        {
            return NotFound();
        }

        var existing = _repos.Values.FirstOrDefault(r => r.Parent == parentName && r.FullName.StartsWith(Login + "/", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return Reply(HttpStatusCode.Accepted, RepoJson(existing).ToJsonString());
        }

        var fork = new FakeRepo($"{Login}/{ForkName ?? parentName.Split('/')[1]}", parent.DefaultBranch, parentName) { NotReadyReads = ForkReadyAfter };
        fork.Refs[parent.DefaultBranch] = parent.Refs[parent.DefaultBranch];
        _repos[fork.FullName] = fork;
        return Reply(HttpStatusCode.Accepted, RepoJson(fork).ToJsonString());
    }

    private HttpResponseMessage MergeUpstream(FakeRepo repo)
    {
        if (repo.Parent is null)
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Not a fork"}""");
        }

        var parent = _repos[repo.Parent];
        repo.Refs[repo.DefaultBranch] = parent.Refs[parent.DefaultBranch];
        return Ok(new JsonObject { ["message"] = "Successfully fetched and fast-forwarded from upstream", ["merge_type"] = "fast-forward" });
    }

    private HttpResponseMessage GetRef(FakeRepo repo, string branch)
    {
        if (repo.NotReadyReads > 0)
        {
            repo.NotReadyReads--;
            return NotFound();
        }

        return repo.Refs.TryGetValue(branch, out var sha) ? Ok(RefJson(branch, sha)) : NotFound();
    }

    private HttpResponseMessage CreateRef(FakeRepo repo, JsonNode json)
    {
        var branch = ((string)json["ref"]!)["refs/heads/".Length..];
        var sha = (string)json["sha"]!;
        if (repo.Refs.ContainsKey(branch))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Reference already exists"}""");
        }

        if (!_commits.ContainsKey(sha))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Object does not exist"}""");
        }

        repo.Refs[branch] = sha;
        return Created(RefJson(branch, sha));
    }

    private HttpResponseMessage UpdateRef(FakeRepo repo, string branch, JsonNode json)
    {
        var sha = (string)json["sha"]!;
        var force = json["force"]?.GetValue<bool>() == true;
        if (!repo.Refs.TryGetValue(branch, out var current))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Reference does not exist"}""");
        }

        if (!force && !IsAncestor(current, sha))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Update is not a fast forward"}""");
        }

        repo.Refs[branch] = sha;
        return Ok(RefJson(branch, sha));
    }

    /// <summary><c>compare/base...head</c>: <c>ahead</c> when base is an ancestor of head, <c>identical</c>, else <c>diverged</c>.</summary>
    private HttpResponseMessage Compare(string range)
    {
        var parts = range.Split("...");
        if (parts.Length != 2 || !_commits.ContainsKey(parts[0]) || !_commits.ContainsKey(parts[1]))
        {
            return NotFound();
        }

        var status = parts[0] == parts[1] ? "identical" : IsAncestor(parts[0], parts[1]) ? "ahead" : "diverged";
        return Ok(new JsonObject { ["status"] = status });
    }

    private bool IsAncestor(string ancestor, string sha)
    {
        for (var at = sha; at is not null; at = _commits[at].Parents.FirstOrDefault())
        {
            if (at == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private HttpResponseMessage GetCommit(string sha) =>
        _commits.TryGetValue(sha, out var commit)
            ? Ok(new JsonObject { ["sha"] = sha, ["message"] = commit.Message, ["tree"] = new JsonObject { ["sha"] = commit.Tree } })
            : NotFound();

    private HttpResponseMessage CreateTree(JsonNode json)
    {
        var baseTree = (string?)json["base_tree"];
        var tree = baseTree is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(_trees[baseTree], StringComparer.Ordinal);
        foreach (var entry in json["tree"]!.AsArray())
        {
            var path = (string)entry!["path"]!;
            if (entry["sha"] is null)
            {
                tree.Remove(path);
            }
            else
            {
                tree[path] = (string)entry["sha"]!;
            }
        }

        return Created(new JsonObject { ["sha"] = PutTree(tree) });
    }

    private HttpResponseMessage CreateCommit(JsonNode json)
    {
        var tree = (string)json["tree"]!;
        var parents = json["parents"]!.AsArray().Select(p => (string)p!).ToList();
        return Created(new JsonObject { ["sha"] = PutCommit(_trees[tree], parents, (string)json["message"]!) });
    }

    private HttpResponseMessage Contents(FakeRepo repo, string path, string? reference, bool raw)
    {
        var sha = reference is null ? repo.Refs[repo.DefaultBranch] : repo.Refs.GetValueOrDefault(reference) ?? reference;
        if (!_commits.TryGetValue(sha, out var commit))
        {
            return NotFound();
        }

        var tree = _trees[commit.Tree];
        if (tree.TryGetValue(path, out var blob))
        {
            return raw
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_blobs[blob]) }
                : Ok(new JsonObject { ["type"] = "file", ["path"] = path, ["name"] = path.Split('/')[^1], ["content"] = Convert.ToBase64String(_blobs[blob]), ["encoding"] = "base64" });
        }

        var prefix = path.Length == 0 ? "" : path + "/";
        var children = tree.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..].Split('/'))
            .Select(p => (Name: p[0], IsDir: p.Length > 1))
            .Distinct()
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        return children.Count == 0
            ? NotFound()
            : Ok(new JsonArray([.. children.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["path"] = prefix + c.Name, ["type"] = c.IsDir ? "dir" : "file" })]));
    }

    private HttpResponseMessage CreateRelease(FakeRepo repo, JsonNode json)
    {
        var tag = (string)json["tag_name"]!;
        if (repo.Releases.Any(r => r.Tag == tag))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"resource":"Release","code":"already_exists","field":"tag_name"}]}""");
        }

        var release = new FakeRelease(_nextId++, tag);
        repo.Releases.Add(release);
        return Created(ReleaseJson(repo, release));
    }

    private HttpResponseMessage ListAssets(FakeRepo repo, long id, Dictionary<string, string> query)
    {
        var release = repo.Releases.FirstOrDefault(r => r.Id == id);
        if (release is null)
        {
            return NotFound();
        }

        var page = int.Parse(query.GetValueOrDefault("page", "1"), CultureInfo.InvariantCulture);
        var size = int.Parse(query.GetValueOrDefault("per_page", "30"), CultureInfo.InvariantCulture);
        return Ok(new JsonArray([.. release.Assets.Skip((page - 1) * size).Take(size).Select(a => AssetJson(repo, release, a))]));
    }

    private HttpResponseMessage DeleteAsset(FakeRepo repo, long id)
    {
        foreach (var release in repo.Releases)
        {
            if (release.Assets.RemoveAll(a => a.Id == id) > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        return NotFound();
    }

    private HttpResponseMessage UploadAsset(string repoName, long releaseId, string name, byte[] content)
    {
        if (!_repos.TryGetValue(repoName, out var repo) || repo.Releases.FirstOrDefault(r => r.Id == releaseId) is not { } release)
        {
            return NotFound();
        }

        if (release.Assets.Any(a => a.Name == name))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"resource":"ReleaseAsset","code":"already_exists","field":"name"}]}""");
        }

        var asset = new FakeAsset(_nextId++, name, content);
        release.Assets.Add(asset);
        return Created(AssetJson(repo, release, asset));
    }

    private HttpResponseMessage CreatePull(FakeRepo repo, JsonNode json)
    {
        var head = (string)json["head"]!;
        if (repo.Pulls.Any(p => p.State == "open" && p.Head == head))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, $$"""{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for {{head}}."}]}""");
        }

        var (owner, branch) = (head.Split(':')[0], head.Split(':')[1]);
        if (!_repos.Values.Any(r => r.FullName.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase) && r.Refs.ContainsKey(branch)))
        {
            return Reply(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"resource":"PullRequest","field":"head","code":"invalid"}]}""");
        }

        var number = repo.Pulls.Count + 1;
        var pr = new FakePullRequest(number, (string)json["title"]!, head, (string)json["base"]!, (string)json["body"]!, new Uri($"https://github.com/{repo.FullName}/pull/{number}"))
        {
            MaintainerCanModify = json["maintainer_can_modify"]?.GetValue<bool>() == true,
        };
        repo.Pulls.Add(pr);
        return Created(PullJson(pr));
    }

    private JsonObject RepoJson(FakeRepo repo) => new()
    {
        ["full_name"] = repo.FullName,
        ["name"] = repo.FullName.Split('/')[1],
        ["owner"] = new JsonObject { ["login"] = repo.FullName.Split('/')[0] },
        ["default_branch"] = repo.DefaultBranch,
        ["fork"] = repo.Parent is not null,
        ["html_url"] = $"https://github.com/{repo.FullName}",
    };

    private static JsonObject RefJson(string branch, string sha) => new()
    {
        ["ref"] = "refs/heads/" + branch,
        ["object"] = new JsonObject { ["sha"] = sha, ["type"] = "commit" },
    };

    private JsonObject ReleaseJson(FakeRepo repo, FakeRelease release) => new()
    {
        ["id"] = release.Id,
        ["tag_name"] = release.Tag,
        ["upload_url"] = $"{Root}uploads/repos/{repo.FullName}/releases/{release.Id}/assets{{?name,label}}",
        ["assets"] = new JsonArray([.. release.Assets.Select(a => AssetJson(repo, release, a))]),
    };

    private static JsonNode AssetJson(FakeRepo repo, FakeRelease release, FakeAsset asset) => new JsonObject
    {
        ["id"] = asset.Id,
        ["name"] = asset.Name,
        ["size"] = asset.Content.Length,
        ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(asset.Content)),
        ["browser_download_url"] = $"https://github.com/{repo.FullName}/releases/download/{release.Tag}/{Uri.EscapeDataString(asset.Name)}",
    };

    private static JsonNode PullJson(FakePullRequest pr) => new JsonObject
    {
        ["number"] = pr.Number,
        ["html_url"] = pr.Url.AbsoluteUri,
        ["state"] = pr.State,
        ["title"] = pr.Title,
        ["head"] = new JsonObject { ["label"] = pr.Head },
    };

    private string PutBlob(byte[] content)
    {
        var sha = Convert.ToHexStringLower(SHA1.HashData(content));
        _blobs[sha] = content;
        return sha;
    }

    private string PutTree(Dictionary<string, string> tree)
    {
        var text = string.Join("\n", tree.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} {e.Value}"));
        var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("tree\n" + text)));
        _trees[sha] = new Dictionary<string, string>(tree, StringComparer.Ordinal);
        return sha;
    }

    private string PutCommit(Dictionary<string, string> tree, List<string> parents, string message)
    {
        var treeSha = PutTree(tree);
        var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"commit {treeSha} {string.Join(",", parents)} {message} {_nextId++}")));
        _commits[sha] = new FakeCommit(treeSha, parents, message);
        return sha;
    }

    private static HttpResponseMessage Ok(JsonNode json) => Reply(HttpStatusCode.OK, json.ToJsonString());

    private static HttpResponseMessage Created(JsonNode json) => Reply(HttpStatusCode.Created, json.ToJsonString());

    private static HttpResponseMessage NotFound() => Reply(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"^repos/(?<repo>[^/]+/[^/]+)(?:/(?<rest>.*))?$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^uploads/repos/(?<repo>[^/]+/[^/]+)/releases/(?<id>\d+)/assets$")]
    private static partial Regex UploadPattern();

    [GeneratedRegex(@"^releases/(?<id>\d+)/assets$")]
    private static partial Regex ReleaseAssetsPattern();

    private sealed record FakeCommit(string Tree, List<string> Parents, string Message);

    private sealed record FakeRepo(string FullName, string DefaultBranch, string? Parent)
    {
        public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

        public List<FakeRelease> Releases { get; } = [];

        public List<FakePullRequest> Pulls { get; } = [];

        public int NotReadyReads { get; set; }
    }

    private sealed record FakeRelease(long Id, string Tag)
    {
        public List<FakeAsset> Assets { get; } = [];
    }

    private sealed record FakeAsset(long Id, string Name, byte[] Content);

    private sealed class FakeFailure(string method, Regex pattern, HttpStatusCode status, string body, IReadOnlyDictionary<string, string> headers, int times)
    {
        public string Method => method;

        public Regex Pattern => pattern;

        public HttpStatusCode Status => status;

        public string Body => body;

        public IReadOnlyDictionary<string, string> Headers => headers;

        public int Times { get; set; } = times;
    }
}

/// <summary>A pull request in <see cref="FakeGitHubApi"/>.</summary>
public sealed record FakePullRequest(int Number, string Title, string Head, string Base, string Body, Uri Url)
{
    public string State { get; set; } = "open";

    public bool MaintainerCanModify { get; init; }
}
