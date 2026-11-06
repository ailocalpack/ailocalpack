using System.Text;
using AiLocalPack.Toolkit.Recipes.RenPy;

namespace AiLocalPack.Toolkit.Tests.Recipes.RenPy;

/// <summary>The safe pickle reader and Ren'Py's archive and compiled script containers.</summary>
public sealed class RenPyFormatTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Pickle_that_would_call_a_function_only_names_it()
    {
        // Arrange: Python would run os.system("echo pwned") while loading this.
        var pickle = "cos\nsystem\n(S'echo pwned'\ntR."u8.ToArray();

        // Act
        var value = PickleReader.Read(pickle);

        // Assert
        value.Should().BeOfType<PyObject>().Which.Should().BeEquivalentTo(new
        {
            Type = new PyGlobal("os", "system"),
            Args = new PyTuple(["echo pwned"]),
        });
    }

    [Fact]
    public void Pickle_written_by_the_writer_reads_back()
    {
        // Arrange
        var dict = new PyDict();
        dict.Set("text", "Привет");
        dict.Set("big", 5_000_000_000L);
        dict.Set("flag", false);
        dict.Set("items", new List<object?> { 1L, null, new PyTuple(["a", -2L]) });
        var node = new PyObject(new PyGlobal("renpy.ast", "Say"), new PyTuple([])) { State = dict };

        // Act
        var read = (PyObject)PickleReader.Read(PickleWriter.Write(node))!;

        // Assert
        (read.Type, read.Attr("text"), read.Attr("big"), read.Attr("flag"), read.Attr("items"))
            .Should().BeEquivalentTo((node.Type, (object)"Привет", (object)5_000_000_000L, (object)false, (object)new List<object?> { 1L, null, new PyTuple(["a", -2L]) }));
    }

    [Fact]
    public void Pickle_long_that_fits_reads_as_a_long()
    {
        // Arrange: LONG1 of 0x42642ab4 (Python 2 wrote archive offsets as longs).
        var pickle = new byte[] { 0x80, 0x02, 0x8a, 0x04, 0xb4, 0x2a, 0x64, 0x42, 0x2e };

        // Act
        var value = PickleReader.Read(pickle);

        // Assert
        value.Should().Be(0x42642ab4L);
    }

    [Fact]
    public void Pickle_reads_memoized_objects_by_reference()
    {
        // Arrange: a list memoized, then fetched again into a tuple.
        var pickle = new byte[] { 0x80, 0x02, 0x5d, 0x71, 0x00, 0x68, 0x00, 0x86, 0x2e };

        // Act
        var tuple = (PyTuple)PickleReader.Read(pickle)!;

        // Assert
        tuple[0].Should().BeSameAs(tuple[1]);
    }

    [Fact]
    public void Pickle_state_with_slots_is_read_as_attributes()
    {
        // Arrange: Ren'Py 8 nodes have __slots__, so BUILD gets (None, {slots}).
        var slots = new PyDict();
        slots.Set("what", "Hi");
        var node = new PyObject(new PyGlobal("renpy.ast", "Say"), new PyTuple([])) { State = new PyTuple([null, slots]) };

        // Act
        var read = (PyObject)PickleReader.Read(PickleWriter.Write(node))!;

        // Assert
        read.Attr("what").Should().Be("Hi");
    }

    [Fact]
    public void Truncated_pickle_is_refused()
    {
        // Arrange
        var pickle = new byte[] { 0x80, 0x02, 0x58, 0x10, 0x00, 0x00, 0x00, 0x61 };

        // Act
        var act = () => PickleReader.Read(pickle);

        // Assert
        act.Should().Throw<PickleException>();
    }

    [Fact]
    public void Pickle_with_persistent_ids_is_refused()
    {
        // Arrange
        var pickle = "Pid\n."u8.ToArray();

        // Act
        var act = () => PickleReader.Read(pickle);

        // Assert
        act.Should().Throw<PickleException>().WithMessage("*opcode 0x50*");
    }

    [Fact]
    public void Archive_entries_read_back_byte_for_byte()
    {
        // Arrange
        var path = _dir.Combine("scripts.rpa");
        RenPyArchive.Write(path, new Dictionary<string, byte[]> { ["a.rpyc"] = [1, 2, 3], ["sub/b.rpyc"] = Encoding.UTF8.GetBytes("bé") });

        // Act
        var archive = RenPyArchive.Open(path);

        // Assert
        (archive.Names, archive.Read("sub/b.rpyc")).Should().BeEquivalentTo((new[] { "a.rpyc", "sub/b.rpyc" }, Encoding.UTF8.GetBytes("bé")));
    }

    [Fact]
    public void Archive_with_an_unknown_header_is_refused()
    {
        // Arrange
        _dir.File("custom.rpa", "ALT-1.0 0000000000000022 42424242\n");

        // Act
        var act = () => RenPyArchive.Open(_dir.Combine("custom.rpa"));

        // Assert
        act.Should().Throw<RenPyFormatException>().WithMessage("Unknown archive format 'ALT-1.0'.");
    }

    [Fact]
    public void Archive_with_a_damaged_header_is_refused()
    {
        // Arrange
        _dir.File("damaged.rpa", "RPA-3.0");

        // Act
        var act = () => RenPyArchive.Open(_dir.Combine("damaged.rpa"));

        // Assert
        act.Should().Throw<RenPyFormatException>().WithMessage("The archive header is damaged.");
    }

    [Fact]
    public void Archive_whose_index_points_outside_the_file_is_refused()
    {
        // Arrange
        _dir.File("broken.rpa", "RPA-3.0 00000000000fffff 42424242\n");

        // Act
        var act = () => RenPyArchive.Open(_dir.Combine("broken.rpa"));

        // Assert
        act.Should().Throw<RenPyFormatException>();
    }

    [Fact]
    public void Compiled_script_slots_read_back()
    {
        // Arrange
        var file = RenPyScriptFile.Write(new Dictionary<int, byte[]> { [1] = [1, 2], [2] = [3] });

        // Act
        var slots = (RenPyScriptFile.ReadSlot(file, 1), RenPyScriptFile.ReadSlot(file, 2), RenPyScriptFile.ReadSlot(file, 3));

        // Assert
        slots.Should().BeEquivalentTo((new byte[] { 1, 2 }, new byte[] { 3 }, (byte[]?)null));
    }

    [Fact]
    public void Compiled_script_that_is_not_zlib_is_refused()
    {
        // Arrange
        var file = Encoding.ASCII.GetBytes("RENPY RPC2").Concat(new byte[] { 1, 0, 0, 0, 34, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9 }).ToArray();

        // Act
        var act = () => RenPyScriptFile.ReadSlot(file, 1);

        // Assert
        act.Should().Throw<RenPyFormatException>();
    }
}
