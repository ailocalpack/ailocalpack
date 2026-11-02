using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Steam apps that install like games but are tools or runtimes (redistributables, Proton, Steam Linux Runtime):
/// never shown as games. Matched by known app ID, plus a name prefix so a future Proton or runtime release is
/// skipped before its ID is added here.
/// </summary>
public static partial class SteamTools
{
    private static readonly FrozenSet<uint> KnownIds = FrozenSet.Create<uint>(
        228980, // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime 1.0 (scout)
        1391110, // Steam Linux Runtime 2.0 (soldier)
        1628350, // Steam Linux Runtime 3.0 (sniper)
        858280, // Proton 3.7
        930400, // Proton 3.16
        961940, // Proton 4.2
        1054830, // Proton 4.11
        1113280, // Proton 5.0
        1245040, // Proton 5.13
        1420170, // Proton 6.3
        1580130, // Proton 6.3 (rebuild)
        1887720, // Proton 7.0
        2348590, // Proton 8.0
        2805730, // Proton 9.0
        3658110, // Proton 10.0
        1493710, // Proton Experimental
        2180100, // Proton Hotfix
        1826330, // Proton EasyAntiCheat Runtime
        1161040); // Proton BattlEye Runtime

    /// <summary>Strict enough to leave games such as "Proton Pulse" alone.</summary>
    [GeneratedRegex(@"^(Proton (\d|Experimental|Hotfix|Next|EasyAntiCheat|BattlEye)|Steam Linux Runtime|Steamworks Common Redistributables)")]
    private static partial Regex ToolName();

    public static bool IsTool(uint appId, string? name) =>
        KnownIds.Contains(appId) || (name is not null && ToolName().IsMatch(name));
}
