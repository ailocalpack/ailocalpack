using System.Globalization;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.Godot;

/// <summary>
/// A Godot translation resource (<c>.translation</c>, what Godot's CSV import writes): <c>Translation</c> (keys and
/// texts: Godot 3 a <c>messages</c> string array of key, text pairs, Godot 4 a dictionary) or the compressed
/// <c>PHashTranslation</c> (Godot 3) / <c>OptimizedTranslation</c> (Godot 4), the import default. The compressed kind
/// keeps no keys, only a two-level hash table: <c>hash_table[hash(0, key) % size]</c> points into
/// <c>bucket_table</c> at a bucket (count, hash seed, then per message the key's hash with that seed, offset, stored and
/// full size) and <c>strings</c> holds the texts, each smaz-compressed or as is (sizes equal), with its terminating
/// zero. A message is known by its bucket and hash (<see cref="GodotMessage.Id"/>) and gets its key back when one of the
/// game's own strings hashes to it (<see cref="Recover"/>). <see cref="Write"/> rewrites texts in place: same table,
/// replaced texts stored as is.
/// </summary>
public sealed class GodotTranslation
{
    private const uint Empty = 0xFFFFFFFF;

    private readonly GodotResource _resource;
    private readonly uint[] _hashTable;
    private readonly uint[] _bucketTable;
    private readonly byte[] _strings;
    private readonly List<GodotMessage> _messages;
    private readonly Dictionary<(int Bucket, uint Hash), int> _byHash = [];

    private GodotTranslation(GodotResource resource, string locale, uint[] hashTable, uint[] bucketTable, byte[] strings, List<GodotMessage> messages)
    {
        _resource = resource;
        Locale = locale;
        _hashTable = hashTable;
        _bucketTable = bucketTable;
        _strings = strings;
        _messages = messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Bucket is { } bucket)
            {
                _byHash[(bucket, messages[i].Hash)] = i;
            }
        }
    }

    /// <summary>The <c>locale</c> property (<c>en</c> when absent: Godot leaves a default value out).</summary>
    public string Locale { get; }

    /// <summary>Compressed: keys known only by hash until <see cref="Recover"/> finds them.</summary>
    public bool IsHashed => _hashTable.Length > 0;

    /// <summary>The messages in stored order (compressed: by bucket).</summary>
    public IReadOnlyList<GodotMessage> Messages => _messages;

    /// <summary>Godot's resource classes that are translations this code reads.</summary>
    public static bool IsTranslationClass(string type) => type is "Translation" or "PHashTranslation" or "OptimizedTranslation";

    /// <exception cref="GodotFormatException">Not a translation of a kind this code reads, or damaged.</exception>
    public static GodotTranslation Read(byte[] file)
    {
        var resource = GodotResource.Read(file);
        var main = resource.Main;
        if (!IsTranslationClass(main.Type))
        {
            throw new GodotFormatException(GodotProblem.Unsupported, $"A {main.Type} resource isn't a translation this app reads.");
        }

        var locale = main.Property("locale")?.Value is GodotString { Text: var name } ? name : "en";
        if (main.Type == "Translation")
        {
            var messages = main.Property("messages")?.Value switch
            {
                GodotStringArray { Items: var items } when items.Count % 2 == 0 =>
                    Enumerable.Range(0, items.Count / 2).Select(i => new GodotMessage(items[2 * i], items[2 * i], items[(2 * i) + 1], null, 0)).ToList(),
                GodotDictionary { Entries: var entries } => entries
                    .Select(e => (Key: e.Key as GodotString, Text: e.Value as GodotString))
                    .Where(e => e.Key is not null && e.Text is not null)
                    .Select(e => new GodotMessage(e.Key!.Text, e.Key.Text, e.Text!.Text, null, 0))
                    .ToList(),
                null => [],
                _ => throw new GodotFormatException(GodotProblem.Unsupported, "The translation's messages are stored in a way this app doesn't read."),
            };
            return new GodotTranslation(resource, locale, [], [], [], messages);
        }

        if (main.Property("hash_table")?.Value is not GodotInt32Array hashes
            || main.Property("bucket_table")?.Value is not GodotInt32Array buckets
            || main.Property("strings")?.Value is not GodotByteArray strings)
        {
            throw new GodotFormatException(GodotProblem.Corrupt, $"The {main.Type} lacks its hash table, buckets or strings.");
        }

        var hashTable = hashes.Items.Select(i => (uint)i).ToArray();
        var bucketTable = buckets.Items.Select(i => (uint)i).ToArray();
        var list = new List<GodotMessage>();
        for (var i = 0; i < hashTable.Length; i++)
        {
            var at = hashTable[i];
            if (at == Empty)
            {
                continue;
            }

            if (at + 2L > bucketTable.Length || at + 2L + (bucketTable[at] * 4L) > bucketTable.Length)
            {
                throw new GodotFormatException(GodotProblem.Corrupt, $"Bucket {i} of the translation lies outside its table.");
            }

            for (var j = 0; j < bucketTable[at]; j++)
            {
                var element = (int)at + 2 + (j * 4);
                var hash = bucketTable[element];
                var text = Text(strings.Bytes, bucketTable[element + 1], bucketTable[element + 2], bucketTable[element + 3]);
                list.Add(new GodotMessage(HashId(i, hash), null, text, i, hash) { Element = element });
            }
        }

        return new GodotTranslation(resource, locale, hashTable, bucketTable, strings.Bytes, list);
    }

    /// <summary>
    /// Gives compressed messages their keys: each candidate (a string found in the game's files, UTF-8) that hashes to a
    /// message's bucket and hash is its key. Godot hashes the key's bytes as C <c>char</c>s, signed on x86 and unsigned
    /// on ARM builds, so both readings are tried. Returns how many keys were found.
    /// </summary>
    public int Recover(IEnumerable<byte[]> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!IsHashed)
        {
            return 0;
        }

        var found = 0;
        foreach (var candidate in candidates)
        {
            foreach (var signed in candidate.Any(b => b >= 0x80) ? [true, false] : new[] { true })
            {
                var bucket = (int)(Hash(0, candidate, signed) % (uint)_hashTable.Length);
                var at = _hashTable[bucket];
                if (at == Empty)
                {
                    continue;
                }

                var hash = Hash(_bucketTable[at + 1], candidate, signed);
                if (_byHash.TryGetValue((bucket, hash), out var index) && _messages[index].Key is null)
                {
                    _messages[index] = _messages[index] with { Key = Encoding.UTF8.GetString(candidate) };
                    found++;
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>The message <paramref name="key"/> names (a plain translation's key, or a compressed one's by hash), or <c>null</c>.</summary>
    public GodotMessage? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsHashed)
        {
            return _messages.FirstOrDefault(m => m.Key == key);
        }

        var bytes = Encoding.UTF8.GetBytes(key);
        foreach (var signed in new[] { true, false })
        {
            var bucket = (int)(Hash(0, bytes, signed) % (uint)_hashTable.Length);
            if (_hashTable[bucket] is var at && at != Empty && _byHash.TryGetValue((bucket, Hash(_bucketTable[at + 1], bytes, signed)), out var index))
            {
                return _messages[index];
            }
        }

        return null;
    }

    /// <summary>The file again with the texts of <paramref name="texts"/> (by <see cref="GodotMessage.Id"/>) replaced.</summary>
    public byte[] Write(IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var main = _resource.Main;
        if (!IsHashed)
        {
            var value = main.Property("messages")!.Value switch
            {
                GodotStringArray { Items: var items } => new GodotStringArray(
                    [.. items.Select((item, i) => i % 2 == 1 && texts.TryGetValue(items[i - 1], out var t) ? t : item)]),
                GodotDictionary { Entries: var entries } => (GodotValue)new GodotDictionary(
                    [.. entries.Select(e => e.Key is GodotString key && e.Value is GodotString text && texts.TryGetValue(key.Text, out var t)
                        ? new KeyValuePair<GodotValue, GodotValue>(e.Key, text with { Text = t })
                        : e)]),
                var other => other,
            };
            return _resource.Write(new Dictionary<string, byte[]> { ["messages"] = value.Encode() });
        }

        var buckets = (uint[])_bucketTable.Clone();
        using var strings = new MemoryStream(_strings.Length);
        foreach (var message in _messages)
        {
            var element = message.Element;
            var offset = (uint)strings.Length;
            if (texts.TryGetValue(message.Id, out var text))
            {
                var bytes = Encoding.UTF8.GetBytes(text + "\0");
                strings.Write(bytes);
                (buckets[element + 1], buckets[element + 2], buckets[element + 3]) = (offset, (uint)bytes.Length, (uint)bytes.Length);
            }
            else
            {
                strings.Write(_strings, (int)_bucketTable[element + 1], (int)_bucketTable[element + 2]);
                buckets[element + 1] = offset;
            }
        }

        return _resource.Write(new Dictionary<string, byte[]>
        {
            ["bucket_table"] = new GodotInt32Array([.. buckets.Select(b => (int)b)]).Encode(),
            ["strings"] = new GodotByteArray(strings.ToArray()).Encode(),
        });
    }

    /// <summary>
    /// A new translation file (fixtures and tests): Godot 3 or 4, plain (<c>Translation</c>) or compressed
    /// (<c>PHashTranslation</c> / <c>OptimizedTranslation</c>, built as Godot's <c>generate</c> does, texts
    /// smaz-compressed where that is shorter). <c>locale</c> is left out for <c>en</c>, as Godot does.
    /// </summary>
    public static byte[] Create(uint major, string locale, IReadOnlyList<KeyValuePair<string, string>> messages, bool hashed)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var properties = new List<(string, byte[])>();
        if (locale != "en")
        {
            properties.Add(("locale", new GodotString(locale).Encode()));
        }

        if (!hashed)
        {
            GodotValue value = major >= 4
                ? new GodotDictionary([.. messages.Select(m => new KeyValuePair<GodotValue, GodotValue>(new GodotString(m.Key, true), new GodotString(m.Value, true)))])
                : new GodotStringArray([.. messages.SelectMany(m => new[] { m.Key, m.Value })]);
            properties.Insert(0, ("messages", value.Encode()));
            return GodotResource.Create(major, "Translation", properties);
        }

        var size = NextPrime(messages.Count);
        var buckets = new List<int>[size];
        var keys = messages.Select(m => Encoding.UTF8.GetBytes(m.Key)).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            (buckets[Hash(0, keys[i], true) % (uint)size] ??= []).Add(i);
        }

        using var strings = new MemoryStream();
        var stored = new (uint Offset, uint Size, uint Full)[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var source = Encoding.UTF8.GetBytes(messages[i].Value + "\0");
            var compressed = Smaz.Compress(source);
            var bytes = compressed.Length < source.Length ? compressed : source;
            stored[i] = ((uint)strings.Length, (uint)bytes.Length, (uint)source.Length);
            strings.Write(bytes);
        }

        var hashTable = new int[size];
        var bucketTable = new List<int>();
        for (var b = 0; b < size; b++)
        {
            if (buckets[b] is not { } members)
            {
                hashTable[b] = unchecked((int)Empty);
                continue;
            }

            var seed = 1u;
            while (members.Select(m => Hash(seed, keys[m], true)).Distinct().Count() != members.Count)
            {
                seed++;
            }

            hashTable[b] = bucketTable.Count;
            bucketTable.Add(members.Count);
            bucketTable.Add((int)seed);
            foreach (var m in members)
            {
                bucketTable.AddRange([(int)Hash(seed, keys[m], true), (int)stored[m].Offset, (int)stored[m].Size, (int)stored[m].Full]);
            }
        }

        properties.Insert(0, ("hash_table", new GodotInt32Array(hashTable).Encode()));
        properties.Insert(1, ("bucket_table", new GodotInt32Array(bucketTable).Encode()));
        properties.Insert(2, ("strings", new GodotByteArray(strings.ToArray()).Encode()));
        return GodotResource.Create(major, major >= 4 ? "OptimizedTranslation" : "PHashTranslation", properties);
    }

    /// <summary>Godot's key hash: FNV-style over the bytes of a C string (seed 0 means 0x1000193).</summary>
    public static uint Hash(uint seed, ReadOnlySpan<byte> key, bool signedChars)
    {
        var d = seed == 0 ? 0x1000193u : seed;
        foreach (var b in key)
        {
            d = unchecked((d * 0x1000193) ^ (signedChars ? (uint)(sbyte)b : b));
        }

        return d;
    }

    private static string HashId(int bucket, uint hash) => string.Create(CultureInfo.InvariantCulture, $"#{bucket}:{hash:x8}");

    private static string Text(byte[] strings, uint offset, uint size, uint full)
    {
        if (offset > strings.Length || size > strings.Length - offset || full > 64 << 20)
        {
            throw new GodotFormatException(GodotProblem.Corrupt, "A translated text lies outside the string data.");
        }

        var bytes = strings.AsSpan((int)offset, (int)size);
        var text = size == full ? bytes.ToArray() : Smaz.Decompress(bytes, (int)full);
        var nul = Array.IndexOf(text, (byte)0);
        return Encoding.UTF8.GetString(text, 0, nul < 0 ? text.Length : nul);
    }

    private static int NextPrime(int n)
    {
        for (var candidate = Math.Max(n + 1, 2); ; candidate++)
        {
            var prime = true;
            for (var d = 2; d * d <= candidate && prime; d++)
            {
                prime = candidate % d != 0;
            }

            if (prime)
            {
                return candidate;
            }
        }
    }
}

/// <summary>One message of a <see cref="GodotTranslation"/>.</summary>
/// <param name="Id">The key for a plain translation; <c>#&lt;bucket&gt;:&lt;hash&gt;</c> for a compressed one.</param>
/// <param name="Key">The translation key (what the game passes to <c>tr()</c>), or <c>null</c> when only its hash is known.</param>
/// <param name="Text">The translated text.</param>
/// <param name="Bucket">Compressed: the hash table slot; plain: <c>null</c>.</param>
/// <param name="Hash">Compressed: the key's hash with the bucket's seed.</param>
public sealed record GodotMessage(string Id, string? Key, string Text, int? Bucket, uint Hash)
{
    /// <summary>Compressed: where the message's four words start in the bucket table.</summary>
    internal int Element { get; init; }
}

/// <summary>
/// smaz, the short-string compression Godot's compressed translations use: codebook bytes 0–253, 254 = one verbatim
/// byte, 255 = a run of n + 1 verbatim bytes.
/// </summary>
internal static class Smaz
{
    private static readonly string[] Codebook =
    [
        " ", "the", "e", "t", "a", "of", "o", "and", "i", "n", "s", "e ", "r", " th",
        " t", "in", "he", "th", "h", "he ", "to", "\r\n", "l", "s ", "d", " a", "an",
        "er", "c", " o", "d ", "on", " of", "re", "of ", "t ", ", ", "is", "u", "at",
        "   ", "n ", "or", "which", "f", "m", "as", "it", "that", "\n", "was", "en",
        "  ", " w", "es", " an", " i", "\r", "f ", "g", "p", "nd", " s", "nd ", "ed ",
        "w", "ed", "http://", "for", "te", "ing", "y ", "The", " c", "ti", "r ", "his",
        "st", " in", "ar", "nt", ",", " to", "y", "ng", " h", "with", "le", "al", "to ",
        "b", "ou", "be", "were", " b", "se", "o ", "ent", "ha", "ng ", "their", "\"",
        "hi", "from", " f", "in ", "de", "ion", "me", "v", ".", "ve", "all", "re ",
        "ri", "ro", "is ", "co", "f t", "are", "ea", ". ", "her", " m", "er ", " p",
        "es ", "by", "they", "di", "ra", "ic", "not", "s, ", "d t", "at ", "ce", "la",
        "h ", "ne", "as ", "tio", "on ", "n t", "io", "we", " a ", "om", ", a", "s o",
        "ur", "li", "ll", "ch", "had", "this", "e t", "g ", "e\r\n", " wh", "ere",
        " co", "e o", "a ", "us", " d", "ss", "\n\r\n", "\r\n\r", "=\"", " be", " e",
        "s a", "ma", "one", "t t", "or ", "but", "el", "so", "l ", "e s", "s,", "no",
        "ter", " wa", "iv", "ho", "e a", " r", "hat", "s t", "ns", "ch ", "wh", "tr",
        "ut", "/", "have", "ly ", "ta", " ha", " on", "tha", "-", " l", "ati", "en ",
        "pe", " re", "there", "ass", "si", " fo", "wa", "ec", "our", "who", "its", "z",
        "fo", "rs", ">", "ot", "un", "<", "im", "th ", "nc", "ate", "><", "ver", "ad",
        " we", "ly", "ee", " n", "id", " cl", "ac", "il", "</", "rt", " wi", "div",
        "e, ", " it", "whi", " ma", "ge", "x", "e c", "men", ".com",
    ];

    private static readonly byte[][] Entries = [.. Codebook.Select(Encoding.ASCII.GetBytes)];

    /// <summary>Decodes into at most <paramref name="length"/> bytes (the stored full size); overflow or a cut code → Corrupt.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int length)
    {
        var output = new byte[length];
        var at = 0;
        for (var i = 0; i < input.Length;)
        {
            ReadOnlySpan<byte> piece;
            switch (input[i])
            {
                case 254:
                    piece = input.Slice(i + 1, 1);
                    i += 2;
                    break;
                case 255:
                    var run = input[i + 1] + 1;
                    piece = input.Slice(i + 2, run);
                    i += 2 + run;
                    break;
                default:
                    piece = Entries[input[i]];
                    i++;
                    break;
            }

            if (at + piece.Length > length)
            {
                throw new GodotFormatException(GodotProblem.Corrupt, "A compressed text is longer than its stored size.");
            }

            piece.CopyTo(output.AsSpan(at));
            at += piece.Length;
        }

        return at == length ? output : output[..at];
    }

    /// <summary>Greedy encoder (fixtures): longest codebook match, else verbatim bytes.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>();
        var verbatim = new List<byte>();
        void Flush()
        {
            for (var start = 0; start < verbatim.Count; start += 256)
            {
                var count = Math.Min(256, verbatim.Count - start);
                output.AddRange(count == 1 ? [254, verbatim[start]] : [255, (byte)(count - 1), .. verbatim.GetRange(start, count)]);
            }

            verbatim.Clear();
        }

        for (var i = 0; i < input.Length;)
        {
            var best = -1;
            for (var e = 0; e < Entries.Length; e++)
            {
                if (input[i..].StartsWith(Entries[e]) && (best < 0 || Entries[e].Length > Entries[best].Length))
                {
                    best = e;
                }
            }

            if (best < 0)
            {
                verbatim.Add(input[i++]);
                continue;
            }

            Flush();
            output.Add((byte)best);
            i += Entries[best].Length;
        }

        Flush();
        return [.. output];
    }
}
