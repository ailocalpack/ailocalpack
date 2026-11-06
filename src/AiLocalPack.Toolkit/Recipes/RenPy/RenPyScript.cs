namespace AiLocalPack.Toolkit.Recipes.RenPy;

/// <summary>One statement of a dialogue translation block: a say statement, or another translatable statement kept as its code (e.g. <c>voice</c>).</summary>
internal sealed record RenPyStatement(RenPySay? Say, string Code);

/// <summary>A dialogue translation block as Ren'Py builds it: one say statement (and the translatable statements before it).</summary>
/// <param name="Identifier">Ren'Py's translation identifier (<c>start_915cb944</c>).</param>
/// <param name="Script">The script it comes from, relative to <c>game/</c> (<c>script.rpy</c>), where its translation file goes.</param>
/// <param name="Source">The game file holding the compiled script (<c>game/script.rpyc</c> or <c>game/scripts.rpa</c>).</param>
/// <param name="Location">The statement's <c>file:line</c> as Ren'Py recorded it, or <c>null</c>.</param>
internal sealed record RenPyDialogue(string Identifier, string Script, string Source, string? Location, string? Label, IReadOnlyList<RenPyStatement> Block)
{
    /// <summary>The say statement (the block's last one).</summary>
    public RenPySay Say => Block[^1].Say!;
}

/// <summary>A text translated by Ren'Py's string translation (<c>translate &lt;language&gt; strings</c>): menu choices and <c>_()</c> texts.</summary>
internal sealed record RenPyString(string Text, bool MenuChoice, string Source);

/// <summary>
/// Reads the translatable text of one compiled script (the statements of a <c>.rpyc</c> pickle, see
/// <see cref="PickleReader"/>): dialogue blocks with Ren'Py's identifiers and the strings of string translation. A
/// script compiled by a newer Ren'Py (slot 2) already holds its translation blocks and their identifiers; an older one
/// is restructured here the way Ren'Py does it on load (<c>Restructurer</c> of Ren'Py 6.99 – 7.x): a say statement closes
/// a block with the translatable statements before it; the identifier uses the last non-hidden label seen.
/// </summary>
internal sealed class RenPyScript
{
    private const string Ast = "renpy.ast";

    private readonly string _script;
    private readonly string _source;
    private readonly ISet<string> _taken;
    private readonly HashSet<string> _preexisting = new(StringComparer.Ordinal);
    private readonly List<RenPyDialogue> _dialogues = [];
    private readonly List<RenPyString> _strings = [];
    private string? _label;

    private RenPyScript(string script, string source, ISet<string> taken)
    {
        _script = script;
        _source = source;
        _taken = taken;
    }

    /// <summary>Reads the statements of one script.</summary>
    /// <param name="statements">The second item of the pickled <c>(data, statements)</c>.</param>
    /// <param name="taken">Identifiers of scripts loaded before this one (Ren'Py's load order); this script's are added.</param>
    public static (IReadOnlyList<RenPyDialogue> Dialogues, IReadOnlyList<RenPyString> Strings) Read(
        IReadOnlyList<object?> statements, string script, string source, ISet<string> taken)
    {
        var reader = new RenPyScript(script, source, taken);
        foreach (var say in Walk(statements).Where(n => n.Is(Ast, "Say")))
        {
            if (Text(say.Attr("identifier")) is { } identifier && say.Attr("explicit_identifier") is true)
            {
                reader._preexisting.Add(identifier);
            }
        }

        reader.Restructure(statements);
        reader.ScanStrings(statements);
        return (reader._dialogues, reader._strings);
    }

    /// <summary>The <c>(data, statements)</c> pickle of a script, read safely.</summary>
    /// <exception cref="RenPyFormatException">Not a compiled script this toolkit reads.</exception>
    public static IReadOnlyList<object?> Statements(byte[] rpyc)
    {
        var pickle = RenPyScriptFile.ReadSlot(rpyc, 2) ?? RenPyScriptFile.ReadSlot(rpyc, 1)
            ?? throw new RenPyFormatException("The compiled script has no statements.");
        object? data;
        try
        {
            data = PickleReader.Read(pickle);
        }
        catch (PickleException e)
        {
            throw new RenPyFormatException($"The compiled script can't be read: {e.Message}", e);
        }

        return data is PyTuple { Count: 2 } pair && pair[1] is List<object?> statements
            ? statements
            : throw new RenPyFormatException("The compiled script is not (data, statements).");
    }

    /// <summary>The <c>Restructurer.callback</c> walk over one block.</summary>
    private void Restructure(IReadOnlyList<object?> children)
    {
        var group = new List<PyObject>();
        foreach (var child in children)
        {
            if (child is not PyObject node)
            {
                continue;
            }

            if (node.Is(Ast, "Label") && node.Attr("hide") is not true && Text(node.Attr("name")) is { } name)
            {
                _label = name;
            }

            if (node.Is(Ast, "TranslateSay"))
            {
                Finish(group);
                if (Text(node.Attr("language")) is null)
                {
                    AddKnown(Text(node.Attr("identifier")), [node]);
                }

                continue;
            }

            if (node.Is(Ast, "Translate"))
            {
                Finish(group);
                if (Text(node.Attr("language")) is null && node.Attr("block") is List<object?> block)
                {
                    AddKnown(Text(node.Attr("identifier")), [.. block.OfType<PyObject>()]);
                }

                continue;
            }

            foreach (var inner in ChildBlocks(node))
            {
                Restructure(inner);
            }

            if (node.Is(Ast, "Say"))
            {
                group.Add(node);
                Finish(group);
            }
            else if (node.Attr("translatable") is true)
            {
                group.Add(node);
            }
            else
            {
                Finish(group);
            }
        }

        Finish(group);
    }

    /// <summary><c>create_translate</c>: the block gets its identifier (whether or not it has text to translate, so later suffixes match Ren'Py's).</summary>
    private void Finish(List<PyObject> group)
    {
        if (group.Count == 0)
        {
            return;
        }

        var nodes = group.ToList();
        group.Clear();
        var statements = nodes.Select(Statement).ToList();
        if (statements.Any(s => s is null))
        {
            return;
        }

        var digest = RenPyTranslation.Digest(statements.Select(s => s!.Code));
        var identifier = RenPyTranslation.Identifier(_label, digest, id => _taken.Contains(id) || _preexisting.Contains(id));
        _taken.Add(identifier);
        if (statements.LastOrDefault(s => s!.Say is not null)?.Say?.ExplicitIdentifier is { } explicitId)
        {
            // An 'id' clause names the block (Ren'Py 7.4+); the computed identifier stays reserved as its alternate.
            identifier = explicitId;
            _taken.Add(identifier);
        }

        Add(identifier, nodes, statements!);
    }

    /// <summary>A block a newer Ren'Py already restructured (slot 2), with its identifier.</summary>
    private void AddKnown(string? identifier, IReadOnlyList<PyObject> nodes)
    {
        if (identifier is null)
        {
            return;
        }

        _taken.Add(identifier);
        var statements = nodes.Select(Statement).ToList();
        if (statements.All(s => s is not null))
        {
            Add(identifier, nodes, statements!);
        }
    }

    private void Add(string identifier, IReadOnlyList<PyObject> nodes, IReadOnlyList<RenPyStatement> statements)
    {
        if (statements.Count == 0 || statements[^1].Say is not { What.Length: > 0 })
        {
            // Nothing to translate (e.g. a voice statement with no line after it); the identifier still counts.
            return;
        }

        var say = nodes[^1];
        var location = Text(say.Attr("filename")) is { } file && say.Attr("linenumber") is long line ? $"{file}:{line}" : null;
        _dialogues.Add(new RenPyDialogue(identifier, _script, _source, location, _label, statements));
    }

    /// <summary>A translatable statement as a block entry; <c>null</c> when its code can't be told (an unknown statement type).</summary>
    private static RenPyStatement? Statement(PyObject node)
    {
        if (node.Is(Ast, "Say") || node.Is(Ast, "TranslateSay"))
        {
            var say = new RenPySay(
                Text(node.Attr("who")),
                Strings(node.Attr("attributes")),
                Strings(node.Attr("temporary_attributes")),
                Text(node.Attr("what")) ?? string.Empty,
                node.Attr("interact") is not false,
                node.Attr("explicit_identifier") is true ? Text(node.Attr("identifier")) : null,
                Arguments(node.Attr("arguments")),
                Text(node.Attr("with_")));
            return new RenPyStatement(say, RenPyTranslation.SayCode(say));
        }

        return Text(node.Attr("line")) is { } line ? new RenPyStatement(null, line) : null;
    }

    /// <summary>Menu choices and <c>_()</c> texts anywhere in the script, other languages' translations left out.</summary>
    private void ScanStrings(IReadOnlyList<object?> statements)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object?>(statements.Reverse());
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            switch (item)
            {
                case string code:
                    foreach (var text in RenPyTranslation.TranslatableCalls(code))
                    {
                        _strings.Add(new RenPyString(text, false, _source));
                    }

                    break;
                case PyObject node when seen.Add(node):
                    if (IsOtherTranslation(node))
                    {
                        break;
                    }

                    if (node.Is(Ast, "Menu") && node.Attr("items") is List<object?> items)
                    {
                        foreach (var choice in items.OfType<PyTuple>())
                        {
                            if (Text(choice[0]) is { Length: > 0 } label)
                            {
                                _strings.Add(new RenPyString(label, true, _source));
                            }
                        }
                    }

                    var isSay = node.Is(Ast, "Say") || node.Is(Ast, "TranslateSay");
                    foreach (var value in Values(node.State).Concat(Values(node.Args)).Concat(node.ListItems))
                    {
                        // A line of dialogue is text, not code.
                        if (!(isSay && value is string))
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case PyTuple tuple when seen.Add(tuple):
                    foreach (var value in tuple.Items)
                    {
                        pending.Push(value);
                    }

                    break;
                case List<object?> list when seen.Add(list):
                    foreach (var value in list)
                    {
                        pending.Push(value);
                    }

                    break;
                case PyDict dict when seen.Add(dict):
                    foreach (var (_, value) in dict.Items)
                    {
                        pending.Push(value);
                    }

                    break;
            }
        }
    }

    /// <summary>Translations into a language (the game's own, or a pack's compiled ones): not the game's text.</summary>
    private static bool IsOtherTranslation(PyObject node) =>
        (node.Is(Ast, "Translate") || node.Is(Ast, "TranslateSay") || node.Is(Ast, "TranslateBlock") || node.Is(Ast, "TranslateEarlyBlock")
            || node.Is(Ast, "TranslatePython") || node.Is(Ast, "TranslateString"))
        && Text(node.Attr("language")) is not null;

    private static IEnumerable<object?> Values(object? state) => state switch
    {
        PyDict dict => dict.Items.Select(i => i.Value),
        PyTuple tuple => tuple.Items,
        _ => [state],
    };

    /// <summary>Statement lists nested in a statement (label, init, while, if / menu branches, user statement blocks).</summary>
    private static IEnumerable<IReadOnlyList<object?>> ChildBlocks(PyObject node)
    {
        if (node.Type.Module != Ast)
        {
            yield break;
        }

        if (node.Attr("block") is List<object?> block)
        {
            yield return block;
        }

        if (node.Attr("code_block") is List<object?> code)
        {
            yield return code;
        }

        if (node.Is(Ast, "Menu") && node.Attr("items") is List<object?> items)
        {
            foreach (var item in items.OfType<PyTuple>())
            {
                if (item[2] is List<object?> branch)
                {
                    yield return branch;
                }
            }
        }

        if (node.Is(Ast, "If") && node.Attr("entries") is List<object?> entries)
        {
            foreach (var entry in entries.OfType<PyTuple>())
            {
                if (entry[1] is List<object?> branch)
                {
                    yield return branch;
                }
            }
        }
    }

    /// <summary>All statements, nested ones included (for the <c>id</c> clauses a file already uses).</summary>
    private static IEnumerable<PyObject> Walk(IReadOnlyList<object?> statements)
    {
        foreach (var node in statements.OfType<PyObject>())
        {
            yield return node;
            foreach (var inner in ChildBlocks(node).SelectMany(Walk))
            {
                yield return inner;
            }

            if ((node.Is(Ast, "Translate") || node.Is(Ast, "TranslateSay")) && node.Attr("block") is List<object?> block)
            {
                foreach (var inner in Walk(block))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>Text of a string or a <c>PyExpr</c> (a string subclass pickled with its source as first argument).</summary>
    public static string? Text(object? value) => value switch
    {
        string s => s,
        PyObject { Args: PyTuple { Count: > 0 } args } when args[0] is string s => s,
        PyObject expr when expr.ListItems.Count == 0 && expr.State is string s => s,
        _ => null,
    };

    private static IReadOnlyList<string>? Strings(object? value) => value switch
    {
        PyTuple tuple => [.. tuple.Items.Select(Text).OfType<string>()],
        List<object?> list => [.. list.Select(Text).OfType<string>()],
        _ => null,
    };

    /// <summary><c>ArgumentInfo.get_code()</c>: <c>(a, b=c, *d, **e)</c>.</summary>
    private static string? Arguments(object? value)
    {
        if (value is not PyObject info)
        {
            return null;
        }

        var arguments = info.Attr("arguments") switch
        {
            List<object?> list => list,
            PyTuple tuple => [.. tuple.Items],
            _ => [],
        };
        var starred = Indexes(info.Attr("starred_indexes"));
        var doubleStarred = Indexes(info.Attr("doublestarred_indexes"));
        var parts = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not PyTuple { Count: 2 } pair || Text(pair[1]) is not { } expression)
            {
                continue;
            }

            parts.Add(starred.Contains(i) ? "*" + expression
                : doubleStarred.Contains(i) ? "**" + expression
                : Text(pair[0]) is { } keyword ? $"{keyword}={expression}"
                : expression);
        }

        // Ren'Py before 7.5 kept *args and **kwargs apart.
        if (Text(info.Attr("extrapos")) is { } extraPositional)
        {
            parts.Add("*" + extraPositional);
        }

        if (Text(info.Attr("extrakw")) is { } extraKeywords)
        {
            parts.Add("**" + extraKeywords);
        }

        return "(" + string.Join(", ", parts) + ")";
    }

    private static HashSet<long> Indexes(object? value) => value switch
    {
        List<object?> list => [.. list.OfType<long>()],
        PyTuple tuple => [.. tuple.Items.OfType<long>()],
        _ => [],
    };
}
