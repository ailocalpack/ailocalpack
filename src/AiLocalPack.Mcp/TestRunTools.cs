using System.ComponentModel;
using AiLocalPack.Core.Generation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiLocalPack.Mcp;

/// <summary>
/// <c>test_run</c>, offered only when the server is started with <c>--test-runs &lt;n&gt;</c> (a fix with AI): the app,
/// which started the agent, rebuilds the translation, runs the untranslated and the translated game and answers through
/// <see cref="TestRunMailbox"/>; the tool waits for that answer.
/// </summary>
[McpServerToolType]
public sealed class TestRunTools(ToolkitSession session)
{
    /// <summary>How long the tool waits for the app's answer (two game runs of up to 3 minutes, the rebuild, the font swap).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    [McpServerTool(Name = "test_run", Destructive = false, Idempotent = false)]
    [Description("Starts the game with the current translation and watches it: first the untranslated game (measured once, to learn how long it loads), "
        + "then the translated one for as long again plus a margin (30 s to 3 min). Returns whether it kept running or crashed (exit code, the OS crash report, "
        + "its error output), when it finished loading, the game's own log, and screenshots (PNG paths you can read) of the original and the translation "
        + "once loaded and of the translation's last picture. Store and rebuild first: it builds from the stored translations (an unknown engine: run your "
        + "rebuild script into out/ first). Each call opens the game on the player's screen, so the number of runs is limited. To narrow a crash down, "
        + "use quick runs (one short launch until the game has loaded; a larger budget of their own) with `files` (lay only some rebuilt files) or "
        + "`untranslated` (leave some keys untranslated for this run only, without changing what is stored), then check the fix with a full run.")]
    public async Task<TestRunAnswer> TestRun(
        [Description("Keep the translated game running at least this many seconds (e.g. when it crashes later than the default watch); at most 180.")] int? seconds = null,
        [Description("A quick run: one launch watched only until the game has loaded (about its loading time), no second launch. Passing says only that it gets past loading.")] bool quick = false,
        [Description("Lay only these rebuilt files over the game (paths relative to the game folder, as the answer's `laid` lists them); the others stay original. Default: all.")] string[]? files = null,
        [Description("Leave these string keys untranslated for this run only; the stored translations don't change. Not for a game read by your own scripts (empty translations there instead).")] string[]? untranslated = null,
        CancellationToken cancellationToken = default)
    {
        if (seconds is < 1 or > 180)
        {
            throw new McpException("seconds must be between 1 and 180.");
        }

        if (files is { Length: 0 })
        {
            throw new McpException("files, when given, must name at least one file.");
        }

        var mailbox = new TestRunMailbox(session.Files.Folder);
        int number;
        lock (session.Gate)
        {
            number = mailbox.NextNumber();
            try
            {
                mailbox.Ask(new TestRunAsk(number, seconds, quick, files, untranslated is { Length: > 0 } ? untranslated : null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"The test run can't be asked for: {e.Message}", e);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerTimeout);
        try
        {
            while (true)
            {
                if (mailbox.Answer(number) is { } answer)
                {
                    return answer;
                }

                await Task.Delay(Poll, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("The app didn't answer the test run in time.");
        }
    }
}
