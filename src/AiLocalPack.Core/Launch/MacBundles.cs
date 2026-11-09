using System.Text.RegularExpressions;

namespace AiLocalPack.Core.Launch;

/// <summary>The program inside a macOS <c>.app</c> bundle. Never throws for disk state.</summary>
public static partial class MacBundles
{
    /// <summary>
    /// The bundle's binary: <c>Contents/MacOS/&lt;CFBundleExecutable&gt;</c> from an XML <c>Info.plist</c>, else the only
    /// file in <c>Contents/MacOS</c>; <c>null</c> when neither is there.
    /// </summary>
    public static string? Executable(string bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        var macOs = Path.Combine(bundle, "Contents", "MacOS");
        try
        {
            var plist = Path.Combine(bundle, "Contents", "Info.plist");
            if (File.Exists(plist) && ExecutableKey().Match(File.ReadAllText(plist)) is { Success: true } match)
            {
                var named = Path.Combine(macOs, match.Groups[1].Value.Trim());
                if (File.Exists(named))
                {
                    return named;
                }
            }

            var files = Directory.Exists(macOs) ? Directory.GetFiles(macOs) : [];
            return files.Length == 1 ? files[0] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableKey();
}

/// <summary>Steam's DRM wrapper (SteamStub) on a Windows program. Never throws for disk state.</summary>
public static class SteamStub
{
    /// <summary>
    /// Whether the PE file has SteamStub's <c>.bind</c> section: such a program only runs when Steam starts it from the
    /// installed game, so a copy can't be tried on its own.
    /// </summary>
    public static bool IsWrapped(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            stream.Position = 0x3C;
            var pe = reader.ReadInt32();
            if (pe <= 0 || pe > stream.Length - 24)
            {
                return false;
            }

            stream.Position = pe;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return false;
            }

            stream.Position = pe + 6;
            var sections = reader.ReadUInt16();
            stream.Position = pe + 20;
            var optionalHeader = reader.ReadUInt16();
            var table = pe + 24 + optionalHeader;
            for (var i = 0; i < sections && table + (i + 1) * 40 <= stream.Length; i++)
            {
                stream.Position = table + i * 40;
                var name = reader.ReadBytes(8);
                if (name.AsSpan().TrimEnd((byte)0).SequenceEqual(".bind"u8))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
