using AiLocalPack.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace AiLocalPack.Desktop.Views;

public sealed partial class GamePageView : UserControl
{
    public GamePageView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += (_, _) => UpdateStuck();
        TabBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateStuck();
            }
        };
    }

    /// <summary>The sticky tab bar shows once the in-flow one has scrolled under the top edge.</summary>
    private void UpdateStuck()
    {
        if (DataContext is GamePageViewModel page)
        {
            page.IsTabBarStuck = Scroller.Offset.Y > 0 && Scroller.Offset.Y >= TabBar.Bounds.Top;
        }
    }
}
