using System.Buffers.Binary;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// A Godot binary resource (<c>RSRC</c>, what <c>.translation</c>, <c>.res</c> and <c>.scn</c> files are), Godot 3
/// (format 1–3) and 4 (format 4–6): header, string table, external and internal resources, each internal resource a
/// type and its properties. Every value is walked so its bytes are known (<see cref="GodotProperty.Raw"/>), but only
/// the kinds translations use are read into data (<see cref="GodotValue"/>); <see cref="Write"/> writes the file again
/// with some properties replaced and everything else byte for byte. Compressed (<c>RSCC</c>) and big-endian files are
/// refused. Little-endian, as Godot writes on every platform it exports for.
/// </summary>
public sealed class GodotResource
{
    private const uint FlagUids = 2;
    private const uint FlagRealIsDouble = 4;
    private const uint FlagScriptClass = 8;

    private readonly byte[] _data;
    private readonly int _internalTableStart;

    private GodotResource(byte[] data, int internalTableStart, uint major, IReadOnlyList<string> strings, IReadOnlyList<GodotSubResource> resources)
    {
        _data = data;
        _internalTableStart = internalTableStart;
        EngineMajor = major;
        Strings = strings;
        Resources = resources;
    }

    /// <summary>Godot major version that wrote the file.</summary>
    public uint EngineMajor { get; }

    /// <summary>The file's string table (property names).</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <summary>Internal resources in file order; the last is the file's main resource.</summary>
    public IReadOnlyList<GodotSubResource> Resources { get; }

    public GodotSubResource Main => Resources[^1];

    /// <exception cref="GodotFormatException">Not a binary resource, or damaged.</exception>
    public static GodotResource Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new GodotReader(data, realIsDouble: false);
        try
        {
            var magic = reader.Bytes(4);
            if (magic.SequenceEqual("RSCC"u8))
            {
                throw new GodotFormatException(GodotProblem.Unsupported, "The resource is compressed (RSCC), which this app doesn't read.");
            }

            if (!magic.SequenceEqual("RSRC"u8))
            {
                throw new GodotFormatException(GodotProblem.Corrupt, "Not a Godot binary resource (no RSRC header).");
            }

            if (reader.U32() != 0)
            {
                throw new GodotFormatException(GodotProblem.Unsupported, "The resource is big-endian, which this app doesn't read.");
            }

            reader.U32(); // use_real64 (Godot 3 ignores it; Godot 4 uses the flag below)
            var major = reader.U32();
            reader.U32(); // minor
            var format = reader.U32();
            if (format > 6 || major > 4)
            {
                throw new GodotFormatException(GodotProblem.Unsupported, $"Resource format {format} (Godot {major}) is newer than this app reads.");
            }

            reader.String(); // type
            reader.U64(); // import metadata offset

            // Godot 3 has 14 reserved words here; Godot 4 reads the first three as flags + UID, both zero in Godot 3 files.
            var flags = reader.U32();
            reader.U64();
            if ((flags & FlagScriptClass) != 0)
            {
                reader.String();
            }

            reader.Skip(11 * 4);
            var strings = new string[reader.Count(4)];
            for (var i = 0; i < strings.Length; i++)
            {
                strings[i] = reader.String();
            }

            var externals = reader.Count(8);
            for (var i = 0; i < externals; i++)
            {
                reader.String();
                reader.String();
                if ((flags & FlagUids) != 0)
                {
                    reader.U64();
                }
            }

            var internalTableStart = reader.Position;
            var table = new (string Path, long Offset)[reader.Count(12)];
            for (var i = 0; i < table.Length; i++)
            {
                table[i] = (reader.String(), (long)reader.U64());
            }

            if (table.Length == 0)
            {
                throw new GodotFormatException(GodotProblem.Corrupt, "The resource holds no resource.");
            }

            var values = new GodotReader(data, (flags & FlagRealIsDouble) != 0);
            var resources = new List<GodotSubResource>(table.Length);
            foreach (var (path, offset) in table)
            {
                if (offset < 0 || offset >= data.Length)
                {
                    throw new GodotFormatException(GodotProblem.Corrupt, $"Resource {path} lies outside the file.");
                }

                values.Position = (int)offset;
                var type = values.String();
                var properties = new GodotProperty[values.Count(8)];
                for (var i = 0; i < properties.Length; i++)
                {
                    var name = values.U32();
                    if (name >= strings.Length)
                    {
                        throw new GodotFormatException(GodotProblem.Corrupt, $"A property of {type} names string {name}, past the table.");
                    }

                    var start = values.Position;
                    var value = values.Variant(strings, format, depth: 0);
                    properties[i] = new GodotProperty(strings[name], data[start..values.Position], value);
                }

                resources.Add(new GodotSubResource(path, type, properties));
            }

            return new GodotResource(data, internalTableStart, major, strings, resources);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new GodotFormatException(GodotProblem.Corrupt, "The resource is cut short or damaged.", e);
        }
    }

    /// <summary>
    /// The file again with <paramref name="replaced"/> properties of the main resource (name → encoded value from
    /// <see cref="GodotValue.Encode"/>) in place of theirs; header, string table, external resources and every other
    /// value as they were.
    /// </summary>
    public byte[] Write(IReadOnlyDictionary<string, byte[]> replaced)
    {
        ArgumentNullException.ThrowIfNull(replaced);
        using var output = new MemoryStream(_data.Length + 1024);
        output.Write(_data, 0, _internalTableStart);
        var writer = new BinaryWriter(output);
        writer.Write((uint)Resources.Count);
        var offsetPositions = new long[Resources.Count];
        for (var i = 0; i < Resources.Count; i++)
        {
            GodotValue.WriteString(writer, Resources[i].Path);
            offsetPositions[i] = output.Position;
            writer.Write(0UL);
        }

        var offsets = new long[Resources.Count];
        for (var i = 0; i < Resources.Count; i++)
        {
            offsets[i] = output.Position;
            var resource = Resources[i];
            GodotValue.WriteString(writer, resource.Type);
            writer.Write((uint)resource.Properties.Count);
            foreach (var property in resource.Properties)
            {
                writer.Write((uint)IndexOf(property.Name));
                writer.Write(i == Resources.Count - 1 && replaced.TryGetValue(property.Name, out var value) ? value : property.Raw);
            }
        }

        writer.Write("RSRC"u8);
        for (var i = 0; i < offsets.Length; i++)
        {
            output.Position = offsetPositions[i];
            writer.Write((ulong)offsets[i]);
        }

        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// A new single-resource file (fixtures and tests): Godot 3 (format 3) or 4 (format 5, with UIDs), properties in
    /// order with their encoded values (<see cref="GodotValue.Encode"/>).
    /// </summary>
    public static byte[] Create(uint major, string type, IReadOnlyList<(string Name, byte[] Value)> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        using var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write("RSRC"u8);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(major);
        writer.Write(major >= 4 ? 4u : 1u);
        writer.Write(major >= 4 ? 5u : 3u);
        GodotValue.WriteString(writer, type);
        writer.Write(0UL);
        writer.Write(major >= 4 ? FlagUids | 1 : 0u);
        writer.Write(major >= 4 ? 0x1234_5678_9ABC_DEF0UL : 0UL);
        writer.Write(new byte[11 * 4]);
        var names = properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        writer.Write((uint)names.Count);
        names.ForEach(n => GodotValue.WriteString(writer, n));
        writer.Write(0u); // external resources
        writer.Write(1u);
        GodotValue.WriteString(writer, major >= 4 ? $"local://{type}_main" : "local://0");
        var offsetPosition = output.Position;
        writer.Write(0UL);
        var offset = output.Position;
        GodotValue.WriteString(writer, type);
        writer.Write((uint)properties.Count);
        foreach (var (name, value) in properties)
        {
            writer.Write((uint)names.IndexOf(name));
            writer.Write(value);
        }

        writer.Write("RSRC"u8);
        output.Position = offsetPosition;
        writer.Write((ulong)offset);
        writer.Flush();
        return output.ToArray();
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < Strings.Count; i++)
        {
            if (Strings[i] == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Property {name} isn't in the string table.");
    }
}

/// <summary>A resource inside a <see cref="GodotResource"/> file.</summary>
public sealed record GodotSubResource(string Path, string Type, IReadOnlyList<GodotProperty> Properties)
{
    public GodotProperty? Property(string name) => Properties.FirstOrDefault(p => p.Name == name);
}

/// <param name="Raw">The value's bytes as stored (type code included).</param>
/// <param name="Value">The value read, or <see cref="GodotValue.Other"/> for kinds no translation uses.</param>
public sealed record GodotProperty(string Name, byte[] Raw, GodotValue Value);

/// <summary>A property value of a binary resource, as far as translations need one.</summary>
public abstract record GodotValue
{
    public const uint TypeString = 5;
    public const uint TypeDictionary = 26;
    public const uint TypeByteArray = 31;
    public const uint TypeInt32Array = 32;
    public const uint TypeStringArray = 34;
    public const uint TypeStringName = 44;

    /// <summary>A kind of value read only to skip it.</summary>
    public static GodotValue Other { get; } = new OtherValue();

    /// <summary>The value encoded as a property value (type code first).</summary>
    public abstract byte[] Encode();

    internal static void WriteString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    protected static byte[] Encoded(Action<BinaryWriter> write)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        write(writer);
        writer.Flush();
        return output.ToArray();
    }

    private sealed record OtherValue : GodotValue
    {
        public override byte[] Encode() => throw new NotSupportedException("Only read values can be encoded.");
    }
}

/// <summary>A <c>String</c> (5) or, in Godot 4, a <c>StringName</c> (44).</summary>
public sealed record GodotString(string Text, bool IsName = false) : GodotValue
{
    public override byte[] Encode() => Encoded(w =>
    {
        w.Write(IsName ? TypeStringName : TypeString);
        WriteString(w, Text);
    });
}

/// <summary>A <c>PoolStringArray</c> / <c>PackedStringArray</c>.</summary>
public sealed record GodotStringArray(IReadOnlyList<string> Items) : GodotValue
{
    public override byte[] Encode() => Encoded(w =>
    {
        w.Write(TypeStringArray);
        w.Write((uint)Items.Count);
        foreach (var item in Items)
        {
            WriteString(w, item);
        }
    });
}

/// <summary>A <c>PoolIntArray</c> / <c>PackedInt32Array</c>.</summary>
public sealed record GodotInt32Array(IReadOnlyList<int> Items) : GodotValue
{
    public override byte[] Encode() => Encoded(w =>
    {
        w.Write(TypeInt32Array);
        w.Write((uint)Items.Count);
        foreach (var item in Items)
        {
            w.Write(item);
        }
    });
}

/// <summary>A <c>PoolByteArray</c> / <c>PackedByteArray</c> (padded to 4 bytes when stored).</summary>
public sealed record GodotByteArray(byte[] Bytes) : GodotValue
{
    public override byte[] Encode() => Encoded(w =>
    {
        w.Write(TypeByteArray);
        w.Write((uint)Bytes.Length);
        w.Write(Bytes);
        w.Write(new byte[(4 - (Bytes.Length % 4)) % 4]);
    });
}

/// <summary>A <c>Dictionary</c>, entries in stored order (keys and values as read).</summary>
public sealed record GodotDictionary(IReadOnlyList<KeyValuePair<GodotValue, GodotValue>> Entries) : GodotValue
{
    public override byte[] Encode() => Encoded(w =>
    {
        w.Write(TypeDictionary);
        w.Write((uint)Entries.Count);
        foreach (var (key, value) in Entries)
        {
            w.Write(key.Encode());
            w.Write(value.Encode());
        }
    });
}

/// <summary>Reads the parts of a binary resource, bounds-checked (out of range → an exception the caller maps to Corrupt).</summary>
internal sealed class GodotReader(byte[] data, bool realIsDouble)
{
    private const int MaxDepth = 64;

    public int Position { get; set; }

    public uint U32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public ulong U64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(Position, 8));
        Position += 8;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        var span = data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public void Skip(long count)
    {
        if (count < 0 || count > data.Length - Position)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Position += (int)count;
    }

    /// <summary>A count, checked against what is left of the file (each item at least <paramref name="minItemSize"/> bytes).</summary>
    public int Count(int minItemSize)
    {
        var count = U32();
        if ((ulong)count * (ulong)minItemSize > (ulong)(data.Length - Position))
        {
            throw new GodotFormatException(GodotProblem.Corrupt, $"The resource lists {count} items, more than the file holds.");
        }

        return (int)count;
    }

    /// <summary>u32 length (with the terminating zero) + UTF-8; text up to the first zero, as Godot reads it.</summary>
    public string String()
    {
        var length = (int)U32();
        var bytes = Bytes(length);
        var nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    public GodotValue Variant(IReadOnlyList<string> strings, uint format, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new GodotFormatException(GodotProblem.Corrupt, "Values nest too deep.");
        }

        var real = realIsDouble ? 8 : 4;
        var type = U32();
        switch (type)
        {
            case 1: // nil
            case 42: // callable
            case 43: // signal
                return GodotValue.Other;
            case 2: // bool
            case 3: // int
            case 23: // rid
                Skip(4);
                return GodotValue.Other;
            case 4: // float (real)
                Skip(real);
                return GodotValue.Other;
            case 40: // int64
            case 41: // double
                Skip(8);
                return GodotValue.Other;
            case GodotValue.TypeString:
                return new GodotString(String());
            case GodotValue.TypeStringName:
                return new GodotString(String(), IsName: true);
            case 10: Skip(2L * real); return GodotValue.Other; // vector2
            case 11: Skip(4L * real); return GodotValue.Other; // rect2
            case 12: Skip(3L * real); return GodotValue.Other; // vector3
            case 13: // plane
            case 14: // quaternion
            case 50: // vector4
                Skip(4L * real);
                return GodotValue.Other;
            case 15: // aabb
            case 18: // transform2d
                Skip(6L * real);
                return GodotValue.Other;
            case 16: Skip(9L * real); return GodotValue.Other; // basis
            case 17: Skip(12L * real); return GodotValue.Other; // transform3d
            case 52: Skip(16L * real); return GodotValue.Other; // projection
            case 20: Skip(16); return GodotValue.Other; // color (always float32)
            case 45: Skip(8); return GodotValue.Other; // vector2i
            case 46: // rect2i
            case 51: // vector4i
                Skip(16);
                return GodotValue.Other;
            case 47: Skip(12); return GodotValue.Other; // vector3i
            case 22: // node path
            {
                var names = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position, 2));
                var subnames = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position + 2, 2)) & 0x7FFF;
                Position += 4;
                if (format < 3)
                {
                    subnames++;
                }

                for (var i = 0; i < names + subnames; i++)
                {
                    var id = U32();
                    if ((id & 0x80000000) != 0)
                    {
                        Skip(id & 0x7FFFFFFF);
                    }
                }

                return GodotValue.Other;
            }

            case 24: // object
                switch (U32())
                {
                    case 0:
                        break;
                    case 1:
                        String();
                        String();
                        break;
                    case 2:
                    case 3:
                        Skip(4);
                        break;
                    default:
                        throw new GodotFormatException(GodotProblem.Corrupt, "Unknown object reference.");
                }

                return GodotValue.Other;
            case GodotValue.TypeDictionary:
            {
                var count = (int)(U32() & 0x7FFFFFFF);
                var entries = new List<KeyValuePair<GodotValue, GodotValue>>(Math.Min(count, 1 << 16));
                for (var i = 0; i < count; i++)
                {
                    var key = Variant(strings, format, depth + 1);
                    entries.Add(new(key, Variant(strings, format, depth + 1)));
                }

                return new GodotDictionary(entries);
            }

            case 30: // array
            {
                var count = (int)(U32() & 0x7FFFFFFF);
                for (var i = 0; i < count; i++)
                {
                    Variant(strings, format, depth + 1);
                }

                return GodotValue.Other;
            }

            case GodotValue.TypeByteArray:
            {
                var length = (int)U32();
                var bytes = Bytes(length).ToArray();
                Skip((4 - (length % 4)) % 4);
                return new GodotByteArray(bytes);
            }

            case GodotValue.TypeInt32Array:
            {
                var count = Count(4);
                var items = new int[count];
                for (var i = 0; i < count; i++)
                {
                    items[i] = (int)U32();
                }

                return new GodotInt32Array(items);
            }

            case GodotValue.TypeStringArray:
            {
                var items = new string[Count(4)];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = String();
                }

                return new GodotStringArray(items);
            }

            case 33: Skip(4L * Count(4)); return GodotValue.Other; // float32 array
            case 48: // int64 array
            case 49: // float64 array
                Skip(8L * Count(8));
                return GodotValue.Other;
            case 35: Skip(3L * real * Count(3 * real)); return GodotValue.Other; // vector3 array
            case 36: Skip(16L * Count(16)); return GodotValue.Other; // color array
            case 37: Skip(2L * real * Count(2 * real)); return GodotValue.Other; // vector2 array
            case 53: Skip(4L * real * Count(4 * real)); return GodotValue.Other; // vector4 array
            default:
                throw new GodotFormatException(GodotProblem.Unsupported, $"Value type {type} isn't one this app reads.");
        }
    }
}
