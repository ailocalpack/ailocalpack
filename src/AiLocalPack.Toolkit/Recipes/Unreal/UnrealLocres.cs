using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>
/// Standard UE locres versions 0–3. Preserve namespace/key hashes and source hashes: changing a translation never
/// changes its identity. Format reference: CUE4Parse UE4/Localization and UE4/Objects/Core/i18N (Apache-2.0).
/// Reads are bounded before allocation; custom game formats and future versions fail explicitly.
/// </summary>
internal sealed record UnrealLocres(byte Version, IReadOnlyList<UnrealText> Entries)
{
    private static readonly byte[] Magic = Convert.FromHexString("0E147475674A03FC4A15909DC3377F1B");
    private const int MaxEntries = 1_000_000;

    public static UnrealLocres Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        byte version = 0;
        if (bytes.AsSpan().StartsWith(Magic))
        {
            stream.Position = Magic.Length;
            version = reader.ReadByte();
            if (version > 3)
            {
                throw new InvalidDataException($"Unsupported locres version {version}.");
            }
        }

        var strings = new List<string>();
        long tableOffset = bytes.Length;
        if (version >= 1)
        {
            tableOffset = reader.ReadInt64();
            var saved = stream.Position;
            if (tableOffset == -1)
            {
                // INDEX_NONE is valid for a resource with no lookup table (and therefore no entries).
                tableOffset = bytes.Length;
            }
            else
            {
                if (tableOffset < saved || tableOffset > bytes.Length - 4)
                {
                    throw new InvalidDataException("Invalid locres string table offset.");
                }

                stream.Position = tableOffset;
                var count = Count(reader);
                for (var i = 0; i < count; i++)
                {
                    strings.Add(ReadString(reader));
                    if (version >= 2 && reader.ReadInt32() < 0)
                    {
                        throw new InvalidDataException("Invalid locres reference count.");
                    }
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Unexpected locres trailing data.");
                }

                stream.Position = saved;
            }
        }

        var declared = version >= 2 ? Count(reader) : -1;
        var namespaces = Count(reader);
        var entries = new List<UnrealText>();
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < namespaces; i++)
        {
            var nsHash = version >= 2 ? reader.ReadUInt32() : 0;
            var ns = ReadString(reader);
            var count = Count(reader);
            if (count > MaxEntries - entries.Count)
            {
                throw new InvalidDataException("Too many locres entries.");
            }

            for (var j = 0; j < count; j++)
            {
                var keyHash = version >= 2 ? reader.ReadUInt32() : 0;
                var key = ReadString(reader);
                var sourceHash = reader.ReadUInt32();
                string value;
                if (version >= 1)
                {
                    var index = reader.ReadInt32();
                    if ((uint)index >= strings.Count)
                    {
                        throw new InvalidDataException("Invalid locres string index.");
                    }

                    value = strings[index];
                }
                else
                {
                    value = ReadString(reader);
                }

                if (!seen.Add((ns, key)))
                {
                    throw new InvalidDataException("Duplicate locres namespace/key.");
                }

                entries.Add(new UnrealText(ns, key, nsHash, keyHash, sourceHash, value));
            }
        }

        if (stream.Position != tableOffset || (declared >= 0 && declared != entries.Count))
        {
            throw new InvalidDataException("Invalid locres entry count or layout.");
        }

        return new UnrealLocres(version, entries);
    }

    public byte[] Write(IReadOnlyDictionary<(string Namespace, string Key), string>? replacements = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if (Version > 3)
        {
            throw new InvalidDataException($"Unsupported locres version {Version}.");
        }

        if (Version > 0)
        {
            writer.Write(Magic);
            writer.Write(Version);
        }

        var offsetPosition = stream.Position;
        if (Version >= 1)
        {
            writer.Write(0L);
        }

        if (Version >= 2)
        {
            writer.Write(Entries.Count);
        }

        var groups = Entries.GroupBy(e => e.Namespace, StringComparer.Ordinal).ToList();
        writer.Write(groups.Count);
        var strings = new List<string>();
        foreach (var group in groups)
        {
            if (Version >= 2)
            {
                writer.Write(group.First().NamespaceHash);
            }

            WriteString(writer, group.Key);
            writer.Write(group.Count());
            foreach (var entry in group)
            {
                if (Version >= 2)
                {
                    writer.Write(entry.KeyHash);
                }

                WriteString(writer, entry.Key);
                writer.Write(entry.SourceHash);
                var text = replacements?.GetValueOrDefault((entry.Namespace, entry.Key)) ?? entry.Text;
                if (Version == 0)
                {
                    WriteString(writer, text);
                }
                else
                {
                    writer.Write(strings.Count);
                    strings.Add(text);
                }
            }
        }

        if (Version >= 1)
        {
            var tableOffset = stream.Position;
            writer.Write(strings.Count);
            foreach (var text in strings)
            {
                WriteString(writer, text);
                if (Version >= 2)
                {
                    writer.Write(1);
                }
            }

            stream.Position = offsetPosition;
            writer.Write(tableOffset);
        }

        return stream.ToArray();
    }

    internal static int Count(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries || count > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid Unreal array count.");
        }

        return count;
    }

    internal static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0)
        {
            return "";
        }

        var byteCount = Math.Abs((long)length) * (length < 0 ? 2 : 1);
        if (byteCount > 16 << 20 || byteCount > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid Unreal string length.");
        }

        var bytes = reader.ReadBytes((int)byteCount);
        var terminator = length < 0 ? 2 : 1;
        if (bytes[^1] != 0 || (terminator == 2 && bytes[^2] != 0))
        {
            throw new InvalidDataException("Unterminated Unreal string.");
        }

        Encoding encoding = length < 0 ? new UnicodeEncoding(false, false, true) : new UTF8Encoding(false, true);
        return encoding.GetString(bytes, 0, bytes.Length - terminator);
    }

    internal static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unreal strings cannot contain NUL.");
        }

        // UE's negative length is a count of UTF-16 code units including the terminator.
        writer.Write(-(value.Length + 1));
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((ushort)0);
    }
}

internal sealed record UnrealText(string Namespace, string Key, uint NamespaceHash, uint KeyHash, uint SourceHash, string Text);
