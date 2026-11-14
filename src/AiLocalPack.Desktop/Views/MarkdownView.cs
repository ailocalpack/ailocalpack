using AiLocalPack.Desktop.Markdown;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Views;

/// <summary>
/// A chat message in Markdown (<see cref="ChatMarkdown"/>): one selectable text block per paragraph, heading or code block,
/// lists with their markers, quotes, rules and tables. Looks come from the <c>md*</c> classes in <c>Controls.axaml</c>;
/// links open in the browser (http, https and mailto only).
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>A table column whose cells are all at most this long keeps its own width; longer ones share the rest and wrap.</summary>
    private const int ShortCell = 24;

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>The parsed message (exposed for tests).</summary>
    public IReadOnlyList<MdBlock> Blocks { get; private set; } = [];

    private void Rebuild()
    {
        Blocks = ChatMarkdown.Parse(Markdown);
        AutomationProperties.SetName(this, ChatMarkdown.PlainText(Blocks));
        Child = Stack(Blocks, 0);
    }

    private static StackPanel Stack(IReadOnlyList<MdBlock> blocks, int depth)
    {
        var panel = new StackPanel();
        panel.Classes.Add("md");
        foreach (var block in blocks)
        {
            panel.Children.Add(Block(block, depth));
        }

        return panel;
    }

    private static Control Block(MdBlock block, int depth) => block switch
    {
        MdParagraph p => Text(p.Inlines),
        MdHeading h => Text(h.Inlines, "h" + Math.Min(h.Level, 4)),
        MdCodeBlock c => Code(c),
        MdQuote q => Quote(q, depth),
        MdList l => List(l, depth),
        MdTable t => Table(t),
        _ => Rule(),
    };

    private static MarkdownTextBlock Text(IReadOnlyList<MdInline> inlines, string? kind = null)
    {
        var text = new MarkdownTextBlock(inlines);
        text.Classes.Add("md");
        if (kind is not null)
        {
            text.Classes.Add(kind);
        }

        return text;
    }

    private static Border Code(MdCodeBlock code)
    {
        var text = new SelectableTextBlock { Text = code.Text };
        text.Classes.Add("mdCode");
        var border = new Border { Child = text };
        border.Classes.Add("mdCode");
        return border;
    }

    private static Border Quote(MdQuote quote, int depth)
    {
        var border = new Border { Child = Stack(quote.Blocks, depth) };
        border.Classes.Add("mdQuote");
        return border;
    }

    private static Border Rule()
    {
        var border = new Border();
        border.Classes.Add("mdRule");
        return border;
    }

    private static Grid List(MdList list, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Classes.Add("mdList");
        var bullet = (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
        for (var i = 0; i < list.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock { Text = list.Ordered ? $"{list.Start + i}." : bullet };
            marker.Classes.Add("mdMarker");
            if (list.Ordered)
            {
                marker.Classes.Add("ordered");
            }

            Grid.SetRow(marker, i);
            grid.Children.Add(marker);
            var content = Stack(list.Items[i], depth + 1);
            Grid.SetRow(content, i);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
        }

        return grid;
    }

    private static Border Table(MdTable table)
    {
        var grid = new Grid();
        var columns = table.Header.Count;
        var allShort = true;
        for (var c = 0; c < columns; c++)
        {
            var isShort = table.Rows.Prepend(table.Header).All(r => ChatMarkdown.PlainText(r[c]).Length <= ShortCell);
            allShort &= isShort;
            grid.ColumnDefinitions.Add(new ColumnDefinition(isShort ? GridLength.Auto : GridLength.Star));
        }

        IEnumerable<IReadOnlyList<IReadOnlyList<MdInline>>> rows = [table.Header, .. table.Rows];
        var r = 0;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var text = Text(row[c]);
                text.TextAlignment = table.Align[c] switch { MdAlign.Center => TextAlignment.Center, MdAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
                var cell = new Border { Child = text };
                cell.Classes.Add("mdCell");
                if (r == 0)
                {
                    cell.Classes.Add("head");
                }

                if (r == table.Rows.Count)
                {
                    cell.Classes.Add("last");
                }

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            r++;
        }

        var border = new Border { Child = grid, HorizontalAlignment = allShort ? HorizontalAlignment.Left : HorizontalAlignment.Stretch };
        border.Classes.Add("mdTable");
        return border;
    }
}

/// <summary>A paragraph's text with its bold, italic, code and link runs; a click (not a selection) on a link opens it.</summary>
public sealed class MarkdownTextBlock : SelectableTextBlock
{
    private const string MonoFont = "Menlo, Consolas, DejaVu Sans Mono, monospace";

    private readonly List<(int Start, int End, Uri Url)> _links = [];
    private int _length;

    public MarkdownTextBlock(IReadOnlyList<MdInline> inlines)
    {
        var collection = new InlineCollection();
        Emit(inlines, default, collection);
        Inlines = collection;
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The link at a text position, or <c>null</c> (exposed for tests).</summary>
    public Uri? LinkAt(int position) => _links.FirstOrDefault(l => position >= l.Start && position < l.End).Url;

    /// <summary>The links in this text, in order (exposed for tests).</summary>
    public IReadOnlyList<Uri> Links => [.. _links.Select(l => l.Url)];

    private readonly record struct Style(bool Bold, bool Italic, bool Strike, Uri? Link, bool IsLink);

    private void Emit(IReadOnlyList<MdInline> inlines, Style style, InlineCollection output)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t:
                    output.Add(Run(t.Text, style, code: false));
                    break;
                case MdCode c:
                    output.Add(Run(c.Text, style, code: true));
                    break;
                case MdLineBreak:
                    output.Add(Run("\n", style with { IsLink = false, Link = null }, code: false));
                    break;
                case MdStrong s:
                    Emit(s.Inlines, style with { Bold = true }, output);
                    break;
                case MdEmphasis e:
                    Emit(e.Inlines, style with { Italic = true }, output);
                    break;
                case MdStrike s:
                    Emit(s.Inlines, style with { Strike = true }, output);
                    break;
                case MdLink l:
                    var start = _length;
                    Emit(l.Inlines, style with { Link = l.Url ?? style.Link, IsLink = true }, output);
                    if (l.Url is { } url)
                    {
                        _links.Add((start, _length, url));
                    }

                    break;
            }
        }
    }

    private Run Run(string text, Style style, bool code)
    {
        _length += text.Length;
        var run = new Run(text);
        if (style.Bold)
        {
            run.FontWeight = FontWeight.Bold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (style.Strike)
        {
            run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
        }

        if (code)
        {
            run.FontFamily = new FontFamily(MonoFont);
            run.FontSize = 12.5;
            run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("AppSurfaceBrush"));
        }

        if (style.IsLink && style.Link is not null)
        {
            run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AppAccentBrush"));
            run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        }

        return run;
    }

    private Uri? LinkUnder(PointerEventArgs e)
    {
        if (_links.Count == 0)
        {
            return null;
        }

        var point = e.GetPosition(this) - new Point(Padding.Left, Padding.Top);
        var hit = TextLayout.HitTestPoint(point);
        return hit.IsInside ? LinkAt(hit.TextPosition) : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_links.Count > 0)
        {
            Cursor = LinkUnder(e) is not null ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && SelectionStart == SelectionEnd && LinkUnder(e) is { } url)
        {
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(url);
        }
    }
}
