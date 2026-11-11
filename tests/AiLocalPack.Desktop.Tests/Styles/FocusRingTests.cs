using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Tests.Styles;

/// <summary>The keyboard focus ring follows the focused control's rounded corners (Fluent's default one is square).</summary>
public sealed class FocusRingTests
{
    [Fact]
    public async Task Ring_of_a_rounded_button_is_rounded_around_it()
    {
        await HeadlessUi.RunAsync(() =>
        {
            // Arrange
            var button = new Button { Content = "Install", Classes = { "accent" } };
            var window = new Window { Content = new StackPanel { Children = { new TextBox(), button } }, Width = 400, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Act
            button.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            // Assert
            var ring = AdornerLayer.GetAdornerLayer(button)!.Children.OfType<Border>()
                .Single(b => AdornerLayer.GetAdornedElement(b) == button);
            ring.CornerRadius.Should().Be(new CornerRadius(13));
            window.Close();
        });
    }

    [Fact]
    public async Task Ring_is_drawn_outside_the_control_unclipped()
    {
        await HeadlessUi.RunAsync(() =>
        {
            // Arrange
            var button = new Button { Content = "Install", Classes = { "ghost" } };
            var window = new Window { Content = new StackPanel { Children = { new TextBox(), button } }, Width = 400, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Act
            button.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            // Assert
            var ring = AdornerLayer.GetAdornerLayer(button)!.Children.OfType<Border>()
                .Single(b => AdornerLayer.GetAdornedElement(b) == button);
            AdornerLayer.GetIsClipEnabled(ring).Should().BeFalse();
            window.Close();
        });
    }

    [Fact]
    public async Task Combo_box_gets_the_same_rounded_ring()
    {
        await HeadlessUi.RunAsync(() =>
        {
            // Arrange
            var combo = new ComboBox { ItemsSource = new[] { "Important first", "Name" }, SelectedIndex = 0 };
            var window = new Window { Content = new StackPanel { Children = { new TextBox(), combo } }, Width = 400, Height = 200 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Act
            combo.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            // Assert
            var ring = AdornerLayer.GetAdornerLayer(combo)!.Children.OfType<Border>()
                .Single(b => AdornerLayer.GetAdornedElement(b) == combo);
            ring.CornerRadius.Should().Be(new CornerRadius(11));
            window.Close();
        });
    }
}
