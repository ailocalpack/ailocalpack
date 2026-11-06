using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>A Python tuple read from a pickle.</summary>
internal sealed record PyTuple(IReadOnlyList<object?> Items)
{
    public object? this[int index] => index < Items.Count ? Items[index] : null;

    public int Count => Items.Count;
}

/// <summary>A class or function named by a pickle (<c>module.name</c>); never resolved, never called.</summary>
internal sealed record PyGlobal(string Module, string Name)
{
    public override string ToString() => $"{Module}.{Name}";
}

/// <summary>
/// An object a pickle builds (<c>REDUCE</c>, <c>NEWOBJ</c>, <c>OBJ</c>, <c>INST</c>): only its class name, constructor
/// arguments, the state <c>BUILD</c> gave it and items appended or set on it (list and dict subclasses). Nothing runs.
/// </summary>
internal sealed class PyObject(PyGlobal type, object? args)
{
    public PyGlobal Type { get; } = type;

    public object? Args { get; } = args;

    public object? State { get; set; }

    public List<object?> ListItems { get; } = [];

    public PyDict DictItems { get; } = new();

    public bool Is(string module, string name) => Type.Module == module && Type.Name == name;

    /// <summary>
    /// An attribute from the state: a dict, or <c>(dict, slots)</c> as objects with <c>__slots__</c> pickle it.
    /// <c>null</c> when absent (as Python's class default would mostly be).
    /// </summary>
    public object? Attr(string name)
    {
        switch (State)
        {
            case PyDict dict:
                return dict.Get(name);
            case PyTuple { Count: 2 } pair:
                return (pair[1] as PyDict)?.Get(name) ?? (pair[0] as PyDict)?.Get(name);
            default:
                return null;
        }
    }

    public bool HasAttr(string name) => State switch
    {
        PyDict dict => dict.Contains(name),
        PyTuple { Count: 2 } pair => (pair[1] as PyDict)?.Contains(name) == true || (pair[0] as PyDict)?.Contains(name) == true,
        _ => false,
    };

    public override string ToString() => Type.ToString();
}

/// <summary>A Python dict read from a pickle; keys compared by value for strings, numbers and tuples.</summary>
internal sealed class PyDict
{
    private readonly Dictionary<object, object?> _items = new(PyKeyComparer.Instance);
    private readonly List<object> _order = [];

    public int Count => _items.Count;

    public IEnumerable<KeyValuePair<object, object?>> Items => _order.Select(k => new KeyValuePair<object, object?>(k, _items[k]));

    public void Set(object? key, object? value)
    {
        key ??= PyNone.Value;
        if (!_items.ContainsKey(key))
        {
            _order.Add(key);
        }

        _items[key] = value;
    }

    public object? Get(object key) => _items.GetValueOrDefault(key);

    public bool Contains(object key) => _items.ContainsKey(key);
}

/// <summary>Python's <c>None</c> as a dict key.</summary>
internal sealed class PyNone
{
    public static readonly PyNone Value = new();

    private PyNone()
    {
    }
}

internal sealed class PyKeyComparer : IEqualityComparer<object>
{
    public static readonly PyKeyComparer Instance = new();

    public new bool Equals(object? x, object? y) => (x, y) switch
    {
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        (long a, long b) => a == b,
        (BigInteger a, BigInteger b) => a == b,
        (bool a, bool b) => a == b,
        (double a, double b) => a.Equals(b),
        (PyTuple a, PyTuple b) => a.Count == b.Count && a.Items.Zip(b.Items).All(p => Equals(p.First ?? PyNone.Value, p.Second ?? PyNone.Value)),
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        _ => ReferenceEquals(x, y),
    };

    public int GetHashCode(object obj) => obj switch
    {
        string s => StringComparer.Ordinal.GetHashCode(s),
        long or BigInteger or bool or double => obj.GetHashCode(),
        PyTuple t => t.Items.Aggregate(t.Count, (h, i) => HashCode.Combine(h, GetHashCode(i ?? PyNone.Value))),
        byte[] b => b.Length,
        _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj),
    };
}

/// <summary>The pickle can't be read: damaged, an unsupported opcode, or over the size limits.</summary>
internal sealed class PickleException(string message) : Exception(message);

/// <summary>
/// Reads Python pickles (protocols 0–5) into plain data without running anything: classes are only names
/// (<see cref="PyGlobal"/>), objects are records of their arguments and state (<see cref="PyObject"/>). This is what
/// makes Ren'Py's compiled scripts (<c>.rpyc</c>) and archive indexes (<c>.rpa</c>) safe to read: a pickle loaded by
/// Python can run any code, here it can only describe data. Python 2 byte strings are read as UTF-8 text (Latin-1 when
/// not valid UTF-8), as Ren'Py wrote text and names with them.
/// </summary>
internal static class PickleReader
{
    /// <summary>Most objects one pickle may create (a guard against crafted files; Ren'Py scripts stay far below).</summary>
    private const int MaxObjects = 20_000_000;

    private static readonly object Mark = new();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <param name="bytesAsLatin1">
    /// Read Python 2 byte strings as Latin-1 (one char per byte, so <see cref="Encoding.Latin1"/> gives the bytes back)
    /// instead of UTF-8 text: for binary data such as the <c>.rpa</c> index's file prefixes.
    /// </param>
    public static object? Read(ReadOnlySpan<byte> data, bool bytesAsLatin1 = false)
    {
        var stack = new List<object?>();
        var marks = new Stack<int>();
        var memo = new Dictionary<long, object?>();
        var pos = 0;
        var created = 0;

        while (true)
        {
            if (pos >= data.Length)
            {
                throw new PickleException("The pickle ends without STOP.");
            }

            if (++created > MaxObjects)
            {
                throw new PickleException("The pickle is too large.");
            }

            var op = data[pos++];
            switch (op)
            {
                case 0x80: // PROTO
                    Take(data, ref pos, 1);
                    break;
                case 0x95: // FRAME
                    Take(data, ref pos, 8);
                    break;
                case 0x2e: // STOP
                    return Pop(stack, marks);
                case 0x28: // MARK
                    marks.Push(stack.Count);
                    break;
                case 0x30: // POP
                    if (marks.Count > 0 && marks.Peek() == stack.Count)
                    {
                        marks.Pop();
                    }
                    else
                    {
                        Pop(stack, marks);
                    }

                    break;
                case 0x31: // POP_MARK
                    PopMark(stack, marks);
                    break;
                case 0x32: // DUP
                    stack.Add(Peek(stack));
                    break;
                case 0x4e: // NONE
                    stack.Add(null);
                    break;
                case 0x88: // NEWTRUE
                    stack.Add(true);
                    break;
                case 0x89: // NEWFALSE
                    stack.Add(false);
                    break;
                case 0x4a: // BININT
                    stack.Add((long)BinaryPrimitives.ReadInt32LittleEndian(Take(data, ref pos, 4)));
                    break;
                case 0x4b: // BININT1
                    stack.Add((long)Take(data, ref pos, 1)[0]);
                    break;
                case 0x4d: // BININT2
                    stack.Add((long)BinaryPrimitives.ReadUInt16LittleEndian(Take(data, ref pos, 2)));
                    break;
                case 0x8a: // LONG1
                    stack.Add(Long(Take(data, ref pos, Take(data, ref pos, 1)[0])));
                    break;
                case 0x8b: // LONG4
                    stack.Add(Long(Take(data, ref pos, Length(BinaryPrimitives.ReadInt32LittleEndian(Take(data, ref pos, 4))))));
                    break;
                case 0x49: // INT
                    stack.Add(TextInt(Line(data, ref pos)));
                    break;
                case 0x4c: // LONG
                    stack.Add(TextInt(Line(data, ref pos).TrimEnd('L')));
                    break;
                case 0x46: // FLOAT
                    stack.Add(double.Parse(Line(data, ref pos), NumberStyles.Float, CultureInfo.InvariantCulture));
                    break;
                case 0x47: // BINFLOAT
                    stack.Add(BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref pos, 8)));
                    break;
                case 0x53: // STRING
                    stack.Add(PythonLiteral.DecodeRepr(Line(data, ref pos)));
                    break;
                case 0x54: // BINSTRING
                    stack.Add(Str(Take(data, ref pos, Length(BinaryPrimitives.ReadInt32LittleEndian(Take(data, ref pos, 4)))), bytesAsLatin1));
                    break;
                case 0x55: // SHORT_BINSTRING
                    stack.Add(Str(Take(data, ref pos, Take(data, ref pos, 1)[0]), bytesAsLatin1));
                    break;
                case 0x56: // UNICODE (raw-unicode-escape)
                    stack.Add(PythonLiteral.DecodeRawUnicodeEscape(Line(data, ref pos)));
                    break;
                case 0x58: // BINUNICODE
                    stack.Add(Utf8(Take(data, ref pos, Length(BinaryPrimitives.ReadUInt32LittleEndian(Take(data, ref pos, 4))))));
                    break;
                case 0x8c: // SHORT_BINUNICODE
                    stack.Add(Utf8(Take(data, ref pos, Take(data, ref pos, 1)[0])));
                    break;
                case 0x8d: // BINUNICODE8
                    stack.Add(Utf8(Take(data, ref pos, Length(BinaryPrimitives.ReadUInt64LittleEndian(Take(data, ref pos, 8))))));
                    break;
                case 0x42: // BINBYTES
                    stack.Add(Take(data, ref pos, Length(BinaryPrimitives.ReadUInt32LittleEndian(Take(data, ref pos, 4)))).ToArray());
                    break;
                case 0x43: // SHORT_BINBYTES
                    stack.Add(Take(data, ref pos, Take(data, ref pos, 1)[0]).ToArray());
                    break;
                case 0x8e or 0x96: // BINBYTES8, BYTEARRAY8
                    stack.Add(Take(data, ref pos, Length(BinaryPrimitives.ReadUInt64LittleEndian(Take(data, ref pos, 8)))).ToArray());
                    break;
                case 0x29: // EMPTY_TUPLE
                    stack.Add(new PyTuple([]));
                    break;
                case 0x74: // TUPLE
                    stack.Add(new PyTuple(PopMark(stack, marks)));
                    break;
                case 0x85: // TUPLE1
                    stack.Add(new PyTuple([Pop(stack, marks)]));
                    break;
                case 0x86: // TUPLE2
                {
                    var b = Pop(stack, marks);
                    var a = Pop(stack, marks);
                    stack.Add(new PyTuple([a, b]));
                    break;
                }

                case 0x87: // TUPLE3
                {
                    var c = Pop(stack, marks);
                    var b = Pop(stack, marks);
                    var a = Pop(stack, marks);
                    stack.Add(new PyTuple([a, b, c]));
                    break;
                }

                case 0x5d: // EMPTY_LIST
                    stack.Add(new List<object?>());
                    break;
                case 0x6c: // LIST
                    stack.Add(PopMark(stack, marks));
                    break;
                case 0x61: // APPEND
                {
                    var value = Pop(stack, marks);
                    AppendTo(Peek(stack), [value]);
                    break;
                }

                case 0x65: // APPENDS
                {
                    var values = PopMark(stack, marks);
                    AppendTo(Peek(stack), values);
                    break;
                }

                case 0x7d: // EMPTY_DICT
                    stack.Add(new PyDict());
                    break;
                case 0x64: // DICT
                {
                    var values = PopMark(stack, marks);
                    var dict = new PyDict();
                    SetItems(dict, values);
                    stack.Add(dict);
                    break;
                }

                case 0x73: // SETITEM
                {
                    var value = Pop(stack, marks);
                    var key = Pop(stack, marks);
                    SetItems(Peek(stack), [key, value]);
                    break;
                }

                case 0x75: // SETITEMS
                {
                    var values = PopMark(stack, marks);
                    SetItems(Peek(stack), values);
                    break;
                }

                case 0x8f: // EMPTY_SET
                    stack.Add(new List<object?>());
                    break;
                case 0x90: // ADDITEMS
                {
                    var values = PopMark(stack, marks);
                    AppendTo(Peek(stack), values);
                    break;
                }

                case 0x91: // FROZENSET
                    stack.Add(PopMark(stack, marks));
                    break;
                case 0x67: // GET
                    stack.Add(Memo(memo, TextInt(Line(data, ref pos))));
                    break;
                case 0x68: // BINGET
                    stack.Add(Memo(memo, Take(data, ref pos, 1)[0]));
                    break;
                case 0x6a: // LONG_BINGET
                    stack.Add(Memo(memo, BinaryPrimitives.ReadUInt32LittleEndian(Take(data, ref pos, 4))));
                    break;
                case 0x70: // PUT
                    memo[Convert.ToInt64(TextInt(Line(data, ref pos)), CultureInfo.InvariantCulture)] = Peek(stack);
                    break;
                case 0x71: // BINPUT
                    memo[Take(data, ref pos, 1)[0]] = Peek(stack);
                    break;
                case 0x72: // LONG_BINPUT
                    memo[BinaryPrimitives.ReadUInt32LittleEndian(Take(data, ref pos, 4))] = Peek(stack);
                    break;
                case 0x94: // MEMOIZE
                    memo[memo.Count] = Peek(stack);
                    break;
                case 0x63: // GLOBAL
                {
                    var module = Line(data, ref pos);
                    stack.Add(new PyGlobal(module, Line(data, ref pos)));
                    break;
                }

                case 0x93: // STACK_GLOBAL
                {
                    var name = Pop(stack, marks) as string;
                    var module = Pop(stack, marks) as string;
                    stack.Add(new PyGlobal(module ?? "?", name ?? "?"));
                    break;
                }

                case 0x52: // REDUCE
                {
                    var args = Pop(stack, marks);
                    stack.Add(Reduce(Pop(stack, marks), args));
                    break;
                }

                case 0x81: // NEWOBJ
                {
                    var args = Pop(stack, marks);
                    stack.Add(New(Pop(stack, marks), args));
                    break;
                }

                case 0x92: // NEWOBJ_EX
                {
                    Pop(stack, marks); // keyword arguments
                    var args = Pop(stack, marks);
                    stack.Add(New(Pop(stack, marks), args));
                    break;
                }

                case 0x6f: // OBJ
                {
                    var items = PopMark(stack, marks);
                    if (items.Count == 0)
                    {
                        throw new PickleException("OBJ without a class.");
                    }

                    stack.Add(New(items[0], new PyTuple(items[1..])));
                    break;
                }

                case 0x69: // INST
                {
                    var module = Line(data, ref pos);
                    var name = Line(data, ref pos);
                    stack.Add(new PyObject(new PyGlobal(module, name), new PyTuple(PopMark(stack, marks))));
                    break;
                }

                case 0x62: // BUILD
                {
                    var state = Pop(stack, marks);
                    if (Peek(stack) is PyObject target)
                    {
                        target.State = state;
                    }

                    break;
                }

                default:
                    throw new PickleException($"Unsupported pickle opcode 0x{op:x2} at byte {pos - 1}.");
            }
        }
    }

    /// <summary>A few well-known callables become plain data; anything else is recorded, not called.</summary>
    private static object? Reduce(object? callable, object? args)
    {
        if (callable is not PyGlobal type)
        {
            throw new PickleException("REDUCE on something that isn't a class.");
        }

        var first = (args as PyTuple)?[0];
        switch (type.Module, type.Name)
        {
            case ("copy_reg" or "copyreg", "_reconstructor") when first is PyGlobal cls:
                return new PyObject(cls, null);
            case ("__builtin__" or "builtins", "set" or "frozenset" or "list"):
                return first switch
                {
                    List<object?> list => list,
                    PyTuple values => values.Items.ToList(),
                    _ => [],
                };
            case ("__builtin__" or "builtins", "tuple"):
                return first is List<object?> items ? new PyTuple(items) : new PyTuple([]);
            case ("collections", "OrderedDict" or "defaultdict"):
                return new PyDict();
            case ("_codecs", "encode") when first is string text:
                // Python 3 pickles of Python 2 bytes: encode(text, 'latin1').
                return Encoding.Latin1.GetBytes(text);
            default:
                return new PyObject(type, args);
        }
    }

    private static PyObject New(object? cls, object? args) =>
        cls is PyGlobal type ? new PyObject(type, args) : throw new PickleException("NEWOBJ on something that isn't a class.");

    private static void AppendTo(object? target, IReadOnlyList<object?> values)
    {
        switch (target)
        {
            case List<object?> list:
                list.AddRange(values);
                break;
            case PyObject obj:
                obj.ListItems.AddRange(values);
                break;
            default:
                throw new PickleException("APPEND to something that isn't a list.");
        }
    }

    private static void SetItems(object? target, IReadOnlyList<object?> values)
    {
        var dict = target switch
        {
            PyDict d => d,
            PyObject obj => obj.DictItems,
            _ => throw new PickleException("SETITEM on something that isn't a dict."),
        };
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            dict.Set(values[i], values[i + 1]);
        }
    }

    private static object? Memo(Dictionary<long, object?> memo, object key)
    {
        var index = Convert.ToInt64(key, CultureInfo.InvariantCulture);
        return memo.TryGetValue(index, out var value) ? value : throw new PickleException($"Memo entry {index} is missing.");
    }

    private static object? Pop(List<object?> stack, Stack<int> marks)
    {
        if (stack.Count == 0 || (marks.Count > 0 && marks.Peek() >= stack.Count))
        {
            throw new PickleException("The pickle's stack is empty.");
        }

        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static object? Peek(List<object?> stack) =>
        stack.Count > 0 ? stack[^1] : throw new PickleException("The pickle's stack is empty.");

    private static List<object?> PopMark(List<object?> stack, Stack<int> marks)
    {
        if (marks.Count == 0)
        {
            throw new PickleException("The pickle has no MARK.");
        }

        var start = marks.Pop();
        var items = stack.GetRange(start, stack.Count - start);
        stack.RemoveRange(start, stack.Count - start);
        return items;
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (count < 0 || pos + count > data.Length)
        {
            throw new PickleException("The pickle is truncated.");
        }

        var span = data.Slice(pos, count);
        pos += count;
        return span;
    }

    private static int Length(long length) =>
        length is < 0 or > int.MaxValue ? throw new PickleException("A pickle item is too large.") : (int)length;

    private static int Length(ulong length) =>
        length > int.MaxValue ? throw new PickleException("A pickle item is too large.") : (int)length;

    private static string Line(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = data[pos..].IndexOf((byte)'\n');
        if (end < 0)
        {
            throw new PickleException("The pickle is truncated.");
        }

        var line = Encoding.Latin1.GetString(data.Slice(pos, end));
        pos += end + 1;
        return line;
    }

    private static object TextInt(string text)
    {
        text = text.Trim();
        return text switch
        {
            "00" => false,
            "01" => true,
            _ when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) => value,
            _ when BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big) => big,
            _ => throw new PickleException($"'{text}' is not a number."),
        };
    }

    private static object Long(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0L;
        }

        var value = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
        // Not a ternary: its common type would box the long back as a BigInteger.
        if (value >= long.MinValue && value <= long.MaxValue)
        {
            return (long)value;
        }

        return value;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Lone surrogates written with 'surrogatepass': keep what can be read.
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static string Str(ReadOnlySpan<byte> bytes, bool latin1)
    {
        if (latin1)
        {
            return Encoding.Latin1.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
