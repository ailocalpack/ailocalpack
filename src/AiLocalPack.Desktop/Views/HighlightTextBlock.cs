using AiLocalPack.Desktop.ViewModels.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Views;

/// <summary>A text block that marks where <see cref="Highlight"/> occurs in <see cref="Source"/> (settings search).</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Source));

    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Highlight));

    static HighlightTextBlock()
    {
        SourceProperty.Changed.AddClassHandler<HighlightTextBlock>((t, _) => t.Rebuild());
        HighlightProperty.Changed.AddClassHandler<HighlightTextBlock>((t, _) => t.Rebuild());
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>The highlighted part, or <c>null</c> (exposed for tests).</summary>
    public string? HighlightedText { get; private set; }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    private void Rebuild()
    {
        var text = Source ?? string.Empty;
        var match = SettingsSearch.Find(text, Highlight);
        HighlightedText = match is { } m ? text.Substring(m.Start, m.Length) : null;
        if (match is not { } hit)
        {
            Inlines = null;
            Text = text;
            return;
        }

        var inlines = new InlineCollection();
        if (hit.Start > 0)
        {
            inlines.Add(new Run(text[..hit.Start]));
        }

        var run = new Run(text.Substring(hit.Start, hit.Length));
        run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("AppHighlightBrush"));
        inlines.Add(run);
        if (hit.Start + hit.Length < text.Length)
        {
            inlines.Add(new Run(text[(hit.Start + hit.Length)..]));
        }

        Inlines = inlines;
    }
}
