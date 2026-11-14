using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>A language of an app in a synthetic <c>appinfo.vdf</c>: Steam API name and its flags.</summary>
public sealed record AppInfoLanguage(string ApiName, bool Supported = true, bool Subtitles = false, bool FullAudio = false);

/// <summary>
/// Writes a synthetic Steam client app info cache (<c>appcache/appinfo.vdf</c>, binary KeyValues) in format 27, 28 or 29
/// (29 = keys through a string table at the end), with <c>appinfo/common/supported_languages</c> per app, or the older
/// <c>common/languages</c> list.
/// </summary>
public sealed class SteamAppInfoWriter(int version = 29)
{
    private readonly List<(uint AppId, Action<KeyValueWriter> Write)> _apps = [];

    /// <summary>An app with <c>supported_languages</c>.</summary>
    public SteamAppInfoWriter AddApp(uint appId, string name, params AppInfoLanguage[] languages)
    {
        _apps.Add((appId, kv =>
        {
            kv.Begin("appinfo");
            kv.Int("appid", (int)appId);
            kv.Begin("common");
            kv.String("name", name);
            kv.String("type", "Game");
            kv.Begin("supported_languages");
            foreach (var language in languages)
            {
                kv.Begin(language.ApiName);
                if (language.Supported)
                {
                    kv.String("supported", "true");
                }

                if (language.FullAudio)
                {
                    kv.String("full_audio", "true");
                }

                if (language.Subtitles)
                {
                    kv.String("subtitles", "true");
                }

                kv.End();
            }

            kv.End();
            kv.End();
            kv.End();
        }));
        return this;
    }

    /// <summary>An app with only the older <c>common/languages</c> list (API name → 1).</summary>
    public SteamAppInfoWriter AddAppWithLanguageList(uint appId, params string[] apiNames)
    {
        _apps.Add((appId, kv =>
        {
            kv.Begin("appinfo");
            kv.Begin("common");
            kv.Begin("languages");
            foreach (var name in apiNames)
            {
                kv.Int(name, 1);
            }

            kv.End();
            kv.End();
            kv.End();
        }));
        return this;
    }

    /// <summary>An app with no language data at all.</summary>
    public SteamAppInfoWriter AddAppWithoutLanguages(uint appId)
    {
        _apps.Add((appId, kv =>
        {
            kv.Begin("appinfo");
            kv.Begin("common");
            kv.String("name", "No languages");
            kv.End();
            kv.End();
        }));
        return this;
    }

    public byte[] ToBytes()
    {
        var table = version >= 29 ? new List<string>() : null;
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x07564400u | (uint)(0x20 + version - 20));
        writer.Write(1u);
        long tableOffsetAt = -1;
        if (table is not null)
        {
            tableOffsetAt = body.Position;
            writer.Write(0L);
        }

        foreach (var (appId, write) in _apps)
        {
            var kv = new KeyValueWriter(table);
            write(kv);
            kv.End();
            var data = kv.ToArray();
            var header = version >= 28 ? 60 : 40;
            writer.Write(appId);
            writer.Write((uint)(header + data.Length));
            writer.Write(new byte[header]);
            writer.Write(data);
        }

        writer.Write(0u);
        if (table is not null)
        {
            var offset = body.Position;
            writer.Write((uint)table.Count);
            foreach (var s in table)
            {
                writer.Write(Encoding.UTF8.GetBytes(s));
                writer.Write((byte)0);
            }

            body.Position = tableOffsetAt;
            writer.Write(offset);
        }

        writer.Flush();
        return body.ToArray();
    }

    /// <summary>Writes the file to <c>&lt;steamRoot&gt;/appcache/appinfo.vdf</c> and returns its path.</summary>
    public string WriteTo(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "appcache", "appinfo.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ToBytes());
        return path;
    }

    /// <summary>Binary KeyValues: keys inline or as string-table indexes.</summary>
    public sealed class KeyValueWriter(List<string>? table)
    {
        private readonly MemoryStream _data = new();

        public void Begin(string key)
        {
            _data.WriteByte(0x00);
            Key(key);
        }

        public void End() => _data.WriteByte(0x08);

        public void String(string key, string value)
        {
            _data.WriteByte(0x01);
            Key(key);
            var bytes = Encoding.UTF8.GetBytes(value);
            _data.Write(bytes);
            _data.WriteByte(0);
        }

        public void Int(string key, int value)
        {
            _data.WriteByte(0x02);
            Key(key);
            _data.Write(BitConverter.GetBytes(value));
        }

        public byte[] ToArray() => _data.ToArray();

        private void Key(string key)
        {
            if (table is null)
            {
                _data.Write(Encoding.UTF8.GetBytes(key));
                _data.WriteByte(0);
                return;
            }

            var index = table.IndexOf(key);
            if (index < 0)
            {
                index = table.Count;
                table.Add(key);
            }

            _data.Write(BitConverter.GetBytes(index));
        }
    }
}
