using Avalonia.Headless;

namespace AiLocalPack.Desktop.Tests;

/// <summary>
/// Runs test code on the Avalonia headless UI thread. Used with plain <c>[Fact]</c> instead of
/// Avalonia.Headless.XUnit's <c>[AvaloniaFact]</c>, which is built against xunit.v3 3.x and fails test discovery
/// under xunit.v3 4.x (MissingMethodException). Switch to <c>[AvaloniaFact]</c> once it supports xunit.v3 4.
/// </summary>
internal static class HeadlessUi
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessUi).Assembly);

    public static Task RunAsync(Action test) =>
        Session.Dispatch(test, TestContext.Current.CancellationToken);

    public static Task RunAsync(Func<Task> test) =>
        Session.Dispatch(async () => { await test(); return true; }, TestContext.Current.CancellationToken);
}
