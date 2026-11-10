namespace AiLocalPack.Agents;

/// <summary>
/// What every adapter does for a run that may run commands (<see cref="AgentBuiltinTool.RunCommands"/>, task 57: the
/// agent's own extract / rebuild scripts): the temporary folder is a <see cref="TempFolderName"/> folder inside the
/// working folder (<c>TMPDIR</c>, <c>TMP</c>, <c>TEMP</c>), made before the run, so scripts that write temporary files
/// stay inside the only folder the agent's sandbox lets them write (Claude Code's sandbox also allows the temporary
/// folder; Codex's excludes it).
/// </summary>
public static class AgentSandbox
{
    public const string TempFolderName = ".tmp";

    /// <summary>The run's environment changes (none without commands); makes the temporary folder.</summary>
    /// <exception cref="IOException">The folder can't be made.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder can't be made.</exception>
    public static IReadOnlyDictionary<string, string?> Environment(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.BuiltinTools.Contains(AgentBuiltinTool.RunCommands))
        {
            return new Dictionary<string, string?>();
        }

        var temp = TempFolder(request.WorkingDirectory);
        Directory.CreateDirectory(temp);
        return new Dictionary<string, string?> { ["TMPDIR"] = temp, ["TMP"] = temp, ["TEMP"] = temp };
    }

    public static string TempFolder(string workingDirectory) => Path.Combine(Path.GetFullPath(workingDirectory), TempFolderName);
}
