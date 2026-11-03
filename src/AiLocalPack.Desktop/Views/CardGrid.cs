using Avalonia;
using Avalonia.Controls;

namespace AiLocalPack.Desktop.Views;

/// <summary>
/// Responsive grid of equal cards (library covers): as many columns as fit cards at least <see cref="MinItemWidth"/>
/// wide, cards stretched to fill the row; rows as tall as their tallest card. 1232 px of content (1280 px window) gives 6
/// columns of 176 px.
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(MinItemWidth), 176);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(ColumnSpacing), 18);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(RowSpacing), 22);

    static CardGrid() => AffectsMeasure<CardGrid>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Columns of the last layout.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>Columns that fit <paramref name="width"/>; at least one.</summary>
    public static int ColumnsFor(double width, double minItemWidth, double spacing) =>
        double.IsFinite(width) && width > 0 ? Math.Max(1, (int)Math.Floor((width + spacing) / (minItemWidth + spacing))) : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinItemWidth;
        Columns = ColumnsFor(width, MinItemWidth, ColumnSpacing);
        var itemWidth = ItemWidth(width);
        var height = 0.0;
        var rowHeight = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (i % Columns == Columns - 1 || i == Children.Count - 1)
            {
                height += rowHeight + (i >= Columns ? RowSpacing : 0);
                rowHeight = 0;
            }
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Columns = ColumnsFor(finalSize.Width, MinItemWidth, ColumnSpacing);
        var itemWidth = ItemWidth(finalSize.Width);
        var y = 0.0;
        for (var start = 0; start < Children.Count; start += Columns)
        {
            var row = Children.Skip(start).Take(Columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var col = 0; col < row.Count; col++)
            {
                row[col].Arrange(new Rect(col * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }

    private double ItemWidth(double width) => Math.Max(0, (width - (ColumnSpacing * (Columns - 1))) / Columns);
}

/// <summary>Sizes its child to a fixed height / width ratio of the width it gets (2:3 covers: <c>Ratio="1.5"</c>).</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 1.5);

    static AspectBox() => AffectsMeasure<AspectBox>(RatioProperty);

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 176;
        var size = new Size(width, width * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(finalSize.Width, finalSize.Width * Ratio);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
