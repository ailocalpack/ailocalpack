using AiLocalPack.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Views;

/// <summary>Splash window (design §9). Dragging it anywhere moves it; a timer re-checks whether the running step is slow.</summary>
public sealed partial class SplashWindow : Window
{
    /// <summary>The Details chevron points up while the list is open.</summary>
    public static readonly IValueConverter ChevronTurn =
        new FuncValueConverter<bool, ITransform>(open => new RotateTransform(open ? 180 : 0));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public SplashWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => (DataContext as SplashViewModel)?.CheckSlow();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        PointerPressed += OnPointerPressed;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            BeginMoveDrag(e);
        }
    }
}
