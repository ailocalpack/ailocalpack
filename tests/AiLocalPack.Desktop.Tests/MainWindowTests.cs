using Avalonia.Controls;
using Avalonia.VisualTree;
using AiLocalPack.Desktop.Views;

namespace AiLocalPack.Desktop.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public Task Main_window_opens() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var window = new MainWindow();

        // Act
        window.Show();

        // Assert
        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("ailocalpack");
        window.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "ailocalpack");
        window.Close();
    });
}
