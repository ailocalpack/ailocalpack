using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Tests.Markdown;

/// <summary>A chat reply's Markdown rendered headless: blocks as controls, inlines as runs, links found by position.</summary>
public sealed class MarkdownViewTests
{
    private static (Window Window, MarkdownView View) Show(string markdown)
    {
        var view = new MarkdownView { Markdown = markdown };
        var window = new Window { Content = view, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    [Fact]
    public Task Blocks_become_their_controls() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        const string markdown = "## Done\nText\n\n- a\n- b\n\n```\ncode\n```\n\n> quote\n\n---\n\n| A | B |\n|---|---|\n| 1 | 2 |";

        // Act
        var (window, view) = Show(markdown);

        // Assert
        var stack = (StackPanel)view.Child!;
        stack.Children.Select(c => c.GetType().Name + "." + string.Join('.', c.Classes.Where(k => !k.StartsWith(':'))))
            .Should().Equal("MarkdownTextBlock.md.h2", "MarkdownTextBlock.md", "Grid.mdList", "Border.mdCode", "Border.mdQuote", "Border.mdRule", "Border.mdTable");
        window.Close();
    });

    [Fact]
    public Task Bold_and_code_are_runs_with_their_looks() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        const string markdown = "Use **вы** in `menu.start`";

        // Act
        var (window, view) = Show(markdown);

        // Assert
        var runs = view.GetVisualDescendants().OfType<MarkdownTextBlock>().Single().Inlines!.OfType<Run>().ToList();
        (runs[1].Text, runs[1].FontWeight, runs[3].Text, runs[3].FontFamily.Name.StartsWith("Menlo", StringComparison.Ordinal))
            .Should().Be(("вы", FontWeight.Bold, "menu.start", true));
        window.Close();
    });

    [Fact]
    public Task A_link_is_found_at_its_text() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        const string markdown = "See [the wiki](https://example.org/w) now";

        // Act
        var (window, view) = Show(markdown);

        // Assert
        var text = view.GetVisualDescendants().OfType<MarkdownTextBlock>().Single();
        (text.LinkAt(3), text.LinkAt(4), text.LinkAt(11), text.LinkAt(12))
            .Should().Be(((Uri?)null, new Uri("https://example.org/w"), new Uri("https://example.org/w"), (Uri?)null));
        window.Close();
    });
}
