using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.ReactiveUI;
using Avalonia.Themes.Fluent;
using DotPad.Runtime.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: AvaloniaTestFramework]

namespace DotPad.Runtime.UI.Tests;

// Minimal headless Avalonia app for [AvaloniaFact]/[AvaloniaTheory] tests (docs/TEST-GUIDE-AVALONIA.md).
// Deliberately configures a plain Application rather than the real DotPad.App — the latter's
// OnFrameworkInitializationCompleted wires up the full DI composition root (file system service,
// preferences file, SDK detection, ...), which the test guide explicitly says UI tests shouldn't
// depend on; tests construct the view models they need directly, with faked dependencies.
// FluentTheme is still required though — without it, themed controls (TabControl, ListBox, ...)
// have no ControlTheme to apply and never realize their template parts/content.
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()));
}
