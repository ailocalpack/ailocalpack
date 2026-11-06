using System.Buffers.Binary;
using System.Text;

namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>
/// Writes plain data as a protocol 2 pickle (what Ren'Py 7 writes): <c>null</c>, <c>bool</c>, <c>long</c>/<c>int</c>,
/// <c>string</c>, <c>byte[]</c> (as a Python 2 byte string), <see cref="PyTuple"/>, lists, <see cref="PyDict"/> and
/// <see cref="PyObject"/> (<c>NEWOBJ</c> + <c>BUILD</c>). For the synthetic games of tests and manual checks.
/// </summary>
internal static class PickleWriter
{
    public static byte[] Write(object? value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x80);
        stream.WriteByte(2);
        WriteValue(stream, value);
        stream.WriteByte(0x2e);
        return stream.ToArray();
    }

    private static void WriteValue(MemoryStream s, object? value)
    {
        switch (value)
        {
            case null:
                s.WriteByte(0x4e);
                break;
            case bool b:
                s.WriteByte(b ? (byte)0x88 : (byte)0x89);
                break;
            case int i:
                WriteValue(s, (long)i);
                break;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                s.WriteByte(0x4a);
                Span<byte> int32 = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(int32, (int)l);
                s.Write(int32);
                break;
            case long l:
                s.WriteByte(0x8a);
                s.WriteByte(8);
                Span<byte> int64 = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(int64, l);
                s.Write(int64);
                break;
            case string text:
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                s.WriteByte(0x58);
                WriteLength(s, bytes.Length);
                s.Write(bytes);
                break;
            }

            case byte[] bytes:
                s.WriteByte(0x54);
                WriteLength(s, bytes.Length);
                s.Write(bytes);
                break;
            case PyTuple tuple:
                s.WriteByte(0x28);
                foreach (var item in tuple.Items)
                {
                    WriteValue(s, item);
                }

                s.WriteByte(0x74);
                break;
            case IEnumerable<object?> list:
                s.WriteByte(0x5d);
                s.WriteByte(0x28);
                foreach (var item in list)
                {
                    WriteValue(s, item);
                }

                s.WriteByte(0x65);
                break;
            case PyDict dict:
                s.WriteByte(0x7d);
                s.WriteByte(0x28);
                foreach (var (key, item) in dict.Items)
                {
                    WriteValue(s, key is PyNone ? null : key);
                    WriteValue(s, item);
                }

                s.WriteByte(0x75);
                break;
            case PyObject obj:
                s.WriteByte(0x63);
                s.Write(Encoding.ASCII.GetBytes($"{obj.Type.Module}\n{obj.Type.Name}\n"));
                WriteValue(s, obj.Args as PyTuple ?? new PyTuple([]));
                s.WriteByte(0x81);
                if (obj.State is not null)
                {
                    WriteValue(s, obj.State);
                    s.WriteByte(0x62);
                }

                break;
            default:
                throw new ArgumentException($"Can't pickle a {value.GetType().Name}.", nameof(value));
        }
    }

    private static void WriteLength(MemoryStream s, int length)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        s.Write(bytes);
    }
}
