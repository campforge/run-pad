using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.ReactiveUI;
using DotPad.Runtime.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: AvaloniaTestFramework]

namespace DotPad.Runtime.UI.Tests;

// Minimal headless Avalonia app for [AvaloniaFact]/[AvaloniaTheory] tests (docs/TEST-GUIDE-AVALONIA.md).
// Deliberately configures a plain Application rather than the real DotPad.App — the latter's
// OnFrameworkInitializationCompleted wires up the full DI composition root (file system service,
// preferences file, SDK detection, ...), which the test guide explicitly says UI tests shouldn't
// depend on; tests construct the view models they need directly, with faked dependencies.
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
