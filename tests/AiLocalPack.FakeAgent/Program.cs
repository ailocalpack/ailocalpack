using AiLocalPack.Agents.Fake;

// AiLocalPack.FakeAgent <recording.jsonl>: see FakeAgentReplay for the recording format.
if (args.Length != 1)
{
    await Console.Error.WriteLineAsync("Usage: AiLocalPack.FakeAgent <recording.jsonl>");
    return 2;
}

return await FakeAgentReplay.RunOnConsoleAsync(args[0], FakeAgentReplayer.ForCurrentProcess());
