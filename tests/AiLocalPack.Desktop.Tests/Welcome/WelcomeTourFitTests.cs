using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using AiLocalPack.Desktop.Welcome;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Welcome;

/// <summary>
/// The welcome tour's illustrations in every UI language: no text spills out of the cover or card it is drawn on (long
/// words are trimmed), with the longest translation language name in the language chip.
/// </summary>
[Collection(typeof(UiLanguageSwitchCollection))]
public sealed class WelcomeTourFitTests
{
    public static TheoryData<string, int> LanguagesAndSlides =>
        new([.. UiLanguages.Supported.SelectMany(l => Enumerable.Range(0, WelcomeTourViewModel.SlideCount).Select(i => (l, i)))]);

    /// <summary>Texts drawn in the illustration panel that stick out of their cover or card: "text in container" pairs.</summary>
    private static List<string> Overflowing(Window window)
    {
        var panel = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Illustrations");
        var overflowing = new List<string>();
        foreach (var text in panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            var box = text.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("miniCover") || b.Classes.Contains("card"));
            var topLeft = text.TranslatePoint(default, box)!.Value;
            var bottomRight = text.TranslatePoint(new Point(text.Bounds.Width, text.Bounds.Height), box)!.Value;
            if (topLeft.X < -0.5 || topLeft.Y < -0.5 || bottomRight.X > box.Bounds.Width + 0.5 || bottomRight.Y > box.Bounds.Height + 0.5)
            {
                overflowing.Add($"«{text.Text}» in {box.Name ?? string.Join('.', box.Classes)}");
            }
        }

        return overflowing;
    }

    [Theory]
    [MemberData(nameof(LanguagesAndSlides))]
    public Task Illustration_texts_stay_inside_their_cards(string language, int slide) => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        try
        {
            Localizer.Current.SetLanguage(language);
            var tour = services.GetRequiredService<IDialogService>().Show<WelcomeTourViewModel>();
            tour.GameLanguage.SelectedOption = tour.GameLanguage.Options.MaxBy(o => o.Display.Length);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Act
            tour.Index = slide;
            Dispatcher.UIThread.RunJobs();

            // Assert
            Overflowing(window).Should().BeEmpty();
        }
        finally
        {
            Localizer.Current.SetLanguage(UiLanguages.Fallback);
            window.Close();
        }
    });
}
