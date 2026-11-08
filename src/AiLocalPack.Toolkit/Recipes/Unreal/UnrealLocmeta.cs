using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Unreal;

/// <summary>Bounded reader for the standard locmeta versions 0–2.</summary>
internal sealed record UnrealLocmeta(string NativeCulture, string NativeLocres, IReadOnlyList<string> CompiledCultures)
{
    public static UnrealLocmeta Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (!reader.ReadBytes(16).AsSpan().SequenceEqual(Convert.FromHexString("4FEE4CA1684855836C4C46BD70DA507C")))
        {
            throw new InvalidDataException("Invalid locmeta magic.");
        }

        var version = reader.ReadByte();
        if (version > 2)
        {
            throw new InvalidDataException($"Unsupported locmeta version {version}.");
        }

        var culture = UnrealLocres.ReadString(reader);
        if (culture.Length == 0 || culture.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            throw new InvalidDataException("Invalid native culture in locmeta.");
        }

        var native = UnrealLocres.ReadString(reader);
        var cultures = new List<string>();
        if (version >= 1)
        {
            var count = UnrealLocres.Count(reader);
            for (var i = 0; i < count; i++)
            {
                cultures.Add(UnrealLocres.ReadString(reader));
            }
        }

        if (version >= 2 && reader.ReadInt32() is not (0 or 1))
        {
            throw new InvalidDataException("Invalid locmeta UGC flag.");
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Unexpected locmeta trailing data.");
        }

        return new UnrealLocmeta(culture, native, cultures);
    }
}
