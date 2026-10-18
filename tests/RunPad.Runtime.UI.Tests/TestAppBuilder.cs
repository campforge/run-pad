using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.ReactiveUI;
using Avalonia.Themes.Fluent;
using RunPad.Runtime.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: AvaloniaTestFramework]

namespace RunPad.Runtime.UI.Tests;

// Minimal headless Avalonia app for [AvaloniaFact]/[AvaloniaTheory] tests.
// Deliberately configures a plain Application rather than the real RunPad.App — the latter's
// OnFrameworkInitializationCompleted wires up the full DI composition root (file system service,
// preferences file, SDK detection, ...), which the test guide explicitly says UI tests shouldn't
// depend on; tests construct the view models they need directly, with faked dependencies.
// FluentTheme is still required though — without it, themed controls (TabControl, ListBox, ...)
// have no ControlTheme to apply and never realize their template parts/content. Same reasoning for
// AvaloniaEdit's own Fluent style (registered by App.axaml in the real app) — without it,
// CodeTextEditor/TextArea (both TemplatedControl) never realize their template either, and tests
// that drive real keyboard input into a CodeTextEditor need the resulting TextArea child to
// actually exist in the visual tree to focus it (see CodeTextEditorCompletionIntegrationTests).
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()))
            .AfterSetup(_ => Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://AvaloniaEdit/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
            }))
            // The Markdown preview's viewer, likewise: its scroll viewer is part of its template.
            .AfterSetup(_ => Application.Current!.Styles.Add(new StyleInclude(new Uri("avares://MarkView.Avalonia/"))
            {
                Source = new Uri("avares://MarkView.Avalonia/Themes/MarkdownTheme.axaml"),
            }));
}
