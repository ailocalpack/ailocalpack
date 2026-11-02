using Avalonia;
using Avalonia.Headless;
using AiLocalPack.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace AiLocalPack.Desktop.Tests;

/// <summary>Runs the real <see cref="App"/> (styles, resources) on the headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
