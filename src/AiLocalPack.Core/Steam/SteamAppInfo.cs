using System.Buffers.Binary;
using System.Text;
using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Steam;

/// <summary>What <see cref="SteamAppInfo.ReadLanguages"/> found.</summary>
/// <param name="Apps">App ID → its languages, for each asked app the file describes with a language list.</param>
/// <param name="Problem">Why the file (or its rest) couldn't be read: missing, unknown format, cut short; <c>null</c> when it read fine.</param>
public sealed record SteamAppInfoLanguages(IReadOnlyDictionary<uint, IReadOnlyList<OfficialLanguage>> Apps, string? Problem = null)
{
    public static SteamAppInfoLanguages Missing { get; } = new(new Dictionary<uint, IReadOnlyList<OfficialLanguage>>(), "no appinfo.vdf");
}

/// <summary>
/// Reads the Steam client's app info cache, <c>&lt;Steam&gt;/appcache/appinfo.vdf</c> (binary KeyValues, the same data as
/// the store page, kept for every app the client has seen): versions 27, 28 and 29 (29, from 2024, names keys through a
/// string table at the end). Only <c>appinfo/common/supported_languages</c> (per language <c>supported</c> = interface,
/// <c>subtitles</c>, <c>full_audio</c>) is taken, or the older <c>common/languages</c> list (interface only). Best effort:
/// never throws for disk state, a broken file gives what was read before the break and the problem.
/// </summary>
public static class SteamAppInfo
{
    /// <summary>The cache file of a Steam root.</summary>
    public static string FileIn(string steamRoot) => Path.Combine(steamRoot, "appcache", "appinfo.vdf");

    private const uint Magic27 = 0x07564427;
    private const uint Magic28 = 0x07564428;
    private const uint Magic29 = 0x07564429;

    /// <summary>The languages of <paramref name="apps"/> listed in <paramref name="file"/>.</summary>
    public static SteamAppInfoLanguages ReadLanguages(string file, IReadOnlyCollection<uint> apps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(apps);
        var found = new Dictionary<uint, IReadOnlyList<OfficialLanguage>>();
        if (!File.Exists(file))
        {
            return SteamAppInfoLanguages.Missing;
        }

        var wanted = apps.ToHashSet();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var magic = reader.ReadUInt32();
            if (magic is not (Magic27 or Magic28 or Magic29))
            {
                return new(found, $"unknown appinfo.vdf format 0x{magic:x8}");
            }

            reader.ReadUInt32(); // universe
            string[]? strings = null;
            if (magic == Magic29)
            {
                var tableOffset = reader.ReadInt64();
                var entries = stream.Position;
                strings = ReadStringTable(stream, tableOffset);
                stream.Position = entries;
            }

            var headerSize = magic == Magic27 ? 40 : 60;
            var seen = 0;
            while (seen < wanted.Count)
            {
                var appId = reader.ReadUInt32();
                if (appId == 0)
                {
                    break;
                }

                var size = reader.ReadUInt32();
                var next = stream.Position + size;
                if (size < headerSize || next > stream.Length)
                {
                    return new(found, $"app {appId}: entry runs past the end of the file");
                }

                if (wanted.Contains(appId))
                {
                    seen++;
                    stream.Position += headerSize;
                    var data = reader.ReadBytes((int)(size - headerSize));
                    if (Languages(new KeyValues(data, strings).ReadRoot()) is { Count: > 0 } languages)
                    {
                        found[appId] = languages;
                    }
                }

                stream.Position = next;
            }

            return new(found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
        {
            return new(found, e.Message);
        }
    }

    private static string[] ReadStringTable(FileStream stream, long offset)
    {
        if (offset < 16 || offset >= stream.Length)
        {
            throw new FormatException("the string table offset is outside the file");
        }

        stream.Position = offset;
        var bytes = new byte[stream.Length - offset];
        stream.ReadExactly(bytes);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var strings = new string[count];
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            var end = Array.IndexOf(bytes, (byte)0, at);
            if (end < 0)
            {
                throw new FormatException("the string table is cut short");
            }

            strings[i] = Encoding.UTF8.GetString(bytes, at, end - at);
            at = end + 1;
        }

        return strings;
    }

    private static List<OfficialLanguage>? Languages(Dictionary<string, object> root)
    {
        if (Child(Child(root, "appinfo"), "common") is not { } common)
        {
            return null;
        }

        var result = new List<OfficialLanguage>();
        if (Child(common, "supported_languages") is { } supported)
        {
            foreach (var (name, value) in supported)
            {
                if (SteamLanguageNames.FromApiName(name) is { } tag && value is Dictionary<string, object> flags && result.All(l => l.Tag != tag))
                {
                    result.Add(new OfficialLanguage(tag, IsTrue(flags, "supported"), IsTrue(flags, "subtitles"), IsTrue(flags, "full_audio")));
                }
            }
        }
        else if (Child(common, "languages") is { } listed)
        {
            foreach (var (name, value) in listed)
            {
                if (SteamLanguageNames.FromApiName(name) is { } tag && IsTrue(value) && result.All(l => l.Tag != tag))
                {
                    result.Add(new OfficialLanguage(tag, Interface: true, Subtitles: false, FullAudio: false));
                }
            }
        }

        return result;
    }

    private static Dictionary<string, object>? Child(Dictionary<string, object>? parent, string key) =>
        parent is not null && parent.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

    private static bool IsTrue(Dictionary<string, object> flags, string key) => flags.TryGetValue(key, out var value) && IsTrue(value);

    private static bool IsTrue(object value) => value switch
    {
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        int i => i != 0,
        long l => l != 0,
        _ => false,
    };

    /// <summary>Binary KeyValues of one app entry; keys inline (27, 28) or string-table indexes (29).</summary>
    private sealed class KeyValues(byte[] data, string[]? strings)
    {
        private int _at;

        public Dictionary<string, object> ReadRoot() => ReadObject(depth: 0);

        private Dictionary<string, object> ReadObject(int depth)
        {
            if (depth > 32)
            {
                throw new FormatException("app info nests too deep");
            }

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var type = Byte();
                if (type is 0x08 or 0x0B)
                {
                    return result;
                }

                var key = Key();
                object value = type switch
                {
                    0x00 => ReadObject(depth + 1),
                    0x01 => CString(),
                    0x02 or 0x03 or 0x04 or 0x06 => Int32(),
                    0x07 or 0x0A => Int64(),
                    0x05 => WideString(),
                    _ => throw new FormatException($"unknown app info value type {type}"),
                };
                result.TryAdd(key, value);
            }
        }

        private string Key()
        {
            if (strings is null)
            {
                return CString();
            }

            var index = Int32();
            return (uint)index < strings.Length ? strings[index] : throw new FormatException($"key {index} is not in the string table");
        }

        private byte Byte() => _at < data.Length ? data[_at++] : throw new FormatException("app info entry is cut short");

        private int Int32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_at));
            _at += 4;
            return value;
        }

        private long Int64()
        {
            Need(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(_at));
            _at += 8;
            return value;
        }

        private string CString()
        {
            var end = Array.IndexOf(data, (byte)0, _at);
            if (end < 0)
            {
                throw new FormatException("app info string is cut short");
            }

            var value = Encoding.UTF8.GetString(data, _at, end - _at);
            _at = end + 1;
            return value;
        }

        private string WideString()
        {
            var start = _at;
            while (true)
            {
                Need(2);
                if (data[_at] == 0 && data[_at + 1] == 0)
                {
                    var value = Encoding.Unicode.GetString(data, start, _at - start);
                    _at += 2;
                    return value;
                }

                _at += 2;
            }
        }

        private void Need(int bytes)
        {
            if (_at + bytes > data.Length)
            {
                throw new FormatException("app info entry is cut short");
            }
        }
    }
}
