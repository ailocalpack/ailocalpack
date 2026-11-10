using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>One run of the <see cref="ScriptedAgent"/>: the prompt it got and the strings listed in it.</summary>
internal sealed class ScriptedRun(string prompt, string copyFolder)
{
    public string Prompt { get; } = prompt;

    /// <summary>The run's request (working folder, built-in tools, system prompt).</summary>
    public AgentRunRequest? Request { get; init; }

    /// <summary>The job folder (the working copy's parent), where put_translations would store.</summary>
    public GenerationFiles Files { get; } = new(Path.GetDirectoryName(Path.GetFullPath(copyFolder))!);

    /// <summary>The <c>{key, original, problem?}</c> lines of the prompt.</summary>
    public IReadOnlyList<(string Key, string Original, string? Problem)> Strings { get; } = Parse(prompt);

    public IReadOnlyList<string> Keys => [.. Strings.Select(s => s.Key)];

    /// <summary>Whether <see cref="Store"/> ran.</summary>
    public bool Stored { get; private set; }

    /// <summary>Stores a translation of each listed string (<paramref name="translate"/> returns <c>null</c> to leave one out), as put_translations would.</summary>
    public void Store(Func<string, string, string?>? translate = null)
    {
        translate ??= (_, original) => "RU " + original;
        var table = Files.ReadStrings();
        var strings = new Dictionary<string, string>(table?.Strings ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (key, original, _) in Strings)
        {
            if (translate(key, original) is { } text)
            {
                strings[key] = text;
            }
        }

        Files.WriteStrings(new StringTable("ru", strings));
        Stored = true;
    }

    private static List<(string, string, string?)> Parse(string prompt)
    {
        const string marker = "Strings (one JSON object per line):\n";
        var at = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }

        var list = new List<(string, string, string?)>();
        foreach (var line in prompt[(at + marker.Length)..].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            list.Add((root.GetProperty("key").GetString()!, root.GetProperty("original").GetString()!, root.TryGetProperty("problem", out var p) ? p.GetString() : null));
        }

        return list;
    }
}

/// <summary>An in-process agent whose every run is scripted: it sees the prompt, may store translations, and returns events.</summary>
internal sealed class ScriptedAgent(Func<ScriptedRun, int, CancellationToken, Task<IReadOnlyList<AgentEvent>>>? script = null) : IAgentAdapter
{
    private static readonly AgentInstallation Installation = new("scripted", "/bin/scripted", "1.0.0", AgentSource.Configured);

    private readonly Lock _lock = new();
    private readonly List<ScriptedRun> _runs = [];

    /// <summary>Stores a translation of every string it is given and succeeds.</summary>
    public static ScriptedAgent Translating() => new((run, _, _) =>
    {
        run.Store();
        return Task.FromResult<IReadOnlyList<AgentEvent>>([Usage, Done]);
    });

    public static AgentUsage Usage { get; } = new(1_000, 200, 50, 10, 0.01m);

    public static AgentDone Done { get; } = new(AgentOutcome.Succeeded, "stored");

    public string Id => "scripted";

    public string DisplayName => "Scripted agent";

    public IReadOnlyList<ScriptedRun> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    public Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult<AgentInstallation?>(Installation);

    public Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentAuthStatus(AgentAuthState.SignedIn));

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentInstallation installation, AgentRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = new ScriptedRun(request.Prompt, request.WorkingDirectory) { Request = request };
        int number;
        lock (_lock)
        {
            _runs.Add(run);
            number = _runs.Count;
        }

        var events = script is null ? [Done] : await script(run, number, cancellationToken);
        foreach (var e in events)
        {
            yield return e;
        }
    }
}
