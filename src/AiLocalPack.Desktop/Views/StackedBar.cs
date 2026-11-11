using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Views;

/// <summary>
/// A rounded bar split into three coloured segments by their shares (Settings → Storage: backups, working copies,
/// cache), 2 px apart, on a track. Shares are fractions of the whole; a zero share draws nothing.
/// </summary>
public sealed class StackedBar : Control
{
    public static readonly StyledProperty<double> FirstProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(First));
    public static readonly StyledProperty<double> SecondProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Second));
    public static readonly StyledProperty<double> ThirdProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Third));
    public static readonly StyledProperty<IBrush?> FirstBrushProperty = AvaloniaProperty.Register<StackedBar, IBrush?>(nameof(FirstBrush));
    public static readonly StyledProperty<IBrush?> SecondBrushProperty = AvaloniaProperty.Register<StackedBar, IBrush?>(nameof(SecondBrush));
    public static readonly StyledProperty<IBrush?> ThirdBrushProperty = AvaloniaProperty.Register<StackedBar, IBrush?>(nameof(ThirdBrush));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<StackedBar, IBrush?>(nameof(Track));

    private const double Gap = 2;

    static StackedBar()
    {
        AffectsRender<StackedBar>(FirstProperty, SecondProperty, ThirdProperty, FirstBrushProperty, SecondBrushProperty, ThirdBrushProperty, TrackProperty);
    }

    public double First { get => GetValue(FirstProperty); set => SetValue(FirstProperty, value); }

    public double Second { get => GetValue(SecondProperty); set => SetValue(SecondProperty, value); }

    public double Third { get => GetValue(ThirdProperty); set => SetValue(ThirdProperty, value); }

    public IBrush? FirstBrush { get => GetValue(FirstBrushProperty); set => SetValue(FirstBrushProperty, value); }

    public IBrush? SecondBrush { get => GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }

    public IBrush? ThirdBrush { get => GetValue(ThirdBrushProperty); set => SetValue(ThirdBrushProperty, value); }

    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 12);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = bounds.Height / 2;
        using (context.PushClip(new RoundedRect(bounds, radius)))
        {
            if (Track is { } track)
            {
                context.FillRectangle(track, bounds);
            }

            (double Share, IBrush? Brush)[] segments = [(First, FirstBrush), (Second, SecondBrush), (Third, ThirdBrush)];
            var shown = segments.Where(s => s.Share > 0 && s.Brush is not null).ToList();
            var width = Math.Max(0, bounds.Width - Gap * Math.Max(0, shown.Count - 1));
            var x = 0d;
            foreach (var (share, brush) in shown)
            {
                var w = width * Math.Min(1, share);
                context.FillRectangle(brush!, new Rect(x, 0, w, bounds.Height));
                x += w + Gap;
            }
        }
    }
}
