using System.Text.RegularExpressions;

namespace AiLocalPack.Desktop.Tests;

/// <summary>Guards the icon paths in <c>Styles/</c> (copied next to the tests) against SVG path syntax Avalonia draws wrong.</summary>
public sealed partial class IconGeometryTests
{
    public static TheoryData<string> Files => ["Theme.axaml", "BrandIcons.axaml"];

    [GeneratedRegex("<StreamGeometry x:Key=\"(?<key>[^\"]+)\">(?<data>[^<]+)</StreamGeometry>")]
    private static partial Regex Geometry();

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex Command();

    /// <summary>
    /// A smooth curve (s / S, t / T) right after a command of another kind: SVG reflects no control point there, but Avalonia's
    /// parser draws it off (the old Lucide bell's left side came out as a slanted line).
    /// </summary>
    private static bool HasLoneSmoothCurve(string data)
    {
        var commands = Command().Matches(data).Select(m => char.ToLowerInvariant(m.Value[0])).ToList();
        return commands.Where((c, i) => c == 's' && (i == 0 || commands[i - 1] is not ('c' or 's'))
            || c == 't' && (i == 0 || commands[i - 1] is not ('q' or 't'))).Any();
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void No_icon_starts_a_smooth_curve_after_another_command(string file)
    {
        // Arrange
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "styles", file));

        // Act
        var broken = Geometry().Matches(text).Where(m => HasLoneSmoothCurve(m.Groups["data"].Value)).Select(m => m.Groups["key"].Value);

        // Assert
        broken.Should().BeEmpty();
    }
}
