using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Views;

/// <summary>The Activity panel: ↑/↓ move between rows, Enter opens one (the row is a button), Esc closes the panel.</summary>
public sealed partial class ActivityPanelView : UserControl
{
    public ActivityPanelView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Esc pressed inside the panel.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>The rows' open buttons in order, top to bottom.</summary>
    public IReadOnlyList<Button> Rows =>
        [.. this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("activityRowOpen") && b.IsEffectivelyVisible)];

    /// <summary>Puts the focus on the first row (when the panel opens).</summary>
    public void FocusFirstRow()
    {
        if (Rows.FirstOrDefault() is { } first)
        {
            first.Focus(NavigationMethod.Directional);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                EscapePressed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    private void Move(int step)
    {
        var rows = Rows;
        if (rows.Count == 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var current = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (focused is not null && (ReferenceEquals(rows[i], focused) || rows[i].IsVisualAncestorOf(focused)))
            {
                current = i;
                break;
            }
        }

        var next = current < 0 ? (step > 0 ? 0 : rows.Count - 1) : Math.Clamp(current + step, 0, rows.Count - 1);
        rows[next].Focus(NavigationMethod.Directional);
        rows[next].BringIntoView();
    }
}
