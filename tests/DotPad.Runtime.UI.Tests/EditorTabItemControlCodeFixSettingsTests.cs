using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.UserControls.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #74 (user request 2026-07-21) — Preferences → C# → "code fixes enabled" master switch and
// the excluded-title list, both filtering the lightbulb margin's merged diagnostic-fix +
// refactoring list. Driven through a REAL EditorsTabItemControl against a MOCKED
// IIntelliSenseService (same idiom as EditorTabItemControlRefactoringsTests), reading
// CodeFixMargin.Fixes directly to check exactly which titles survived filtering, not just which
// line has *a* fix (WorstCodeFixSeverityOnLine can't tell two same-severity candidates on different
// lines apart, but this needs to: it's asserting on WHICH one got excluded).
public class EditorTabItemControlCodeFixSettingsTests
{
    private sealed record FakeCodeFixInfo(int Line, string Title, DiagnosticSeverity Severity, string Key) : ICodeFixInfo;

    private static Mock<IPreferencesService> MakePrefs(CSharpLanguageSettings? csharp = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(csharp ?? new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static Mock<IThemeService> MakeTheme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private static (EditorsTabItemControl ctrl, CodeTextEditor editor, CodeFixMargin margin) CreateControl(
        Mock<IPreferencesService> prefs, Mock<IIntelliSenseService> intelliSense)
    {
        var vm = new EditorTabItemViewModel(null, [], prefs.Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense.Object, new CSharpReferenceService(), buildCache.Object, editorBackground: null);
        var host = new Window { Content = ctrl };
        host.Show();

        var editor = ctrl.State.Editor;
        var margin = editor.TextArea.LeftMargins.OfType<CodeFixMargin>().Single();
        return (ctrl, editor, margin);
    }

    [AvaloniaFact]
    public async Task ExcludedCodeFixTitles_Should_HideOnlyTheMatchingEntry_KeepingOthersVisible()
    {
        var settings = new CSharpLanguageSettings { ExcludedCodeFixTitles = ["use explicit type"] };
        var prefs = MakePrefs(settings);
        var intelliSense = new Mock<IIntelliSenseService>();
        var (_, editor, margin) = CreateControl(prefs, intelliSense);

        editor.Text = "line1\nline2\n";

        intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text, (IReadOnlyList<ICodeFixInfo>)
        [
            new FakeCodeFixInfo(1, "Use explicit type instead of 'var'", DiagnosticSeverity.Info, "excluded-key"),
            new FakeCodeFixInfo(2, "Add using System;", DiagnosticSeverity.Warning, "kept-key"),
        ]);

        for (var i = 0; i < 100 && margin.Fixes.Count == 0; i++)
            await Task.Delay(20);

        var titles = margin.Fixes.Select(f => f.Title).ToList();
        Assert.DoesNotContain("Use explicit type instead of 'var'", titles);
        Assert.Contains("Add using System;", titles);
    }

    [AvaloniaFact]
    public async Task CodeFixesDisabled_Should_HideEveryEntry_AndSkipFetchingRefactorings()
    {
        var settings = new CSharpLanguageSettings { CodeFixesEnabled = false };
        var prefs = MakePrefs(settings);
        var intelliSense = new Mock<IIntelliSenseService>();
        intelliSense
            .Setup(s => s.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ICodeFixInfo>?)[new FakeCodeFixInfo(1, "Use expression body", DiagnosticSeverity.Info, "refactor-key")]);
        var (_, editor, margin) = CreateControl(prefs, intelliSense);

        editor.Text = "line1\nline2\n";
        intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text,
            (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(2, "Add using System;", DiagnosticSeverity.Warning, "fix-key")]);
        editor.TextArea.Caret.Offset = 3;

        // Give both the diagnostic-fix handler and the refactoring debounce (400ms) time to run.
        await Task.Delay(600);

        Assert.Empty(margin.Fixes);
        intelliSense.Verify(
            s => s.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [AvaloniaFact]
    public async Task TogglingCodeFixesEnabled_Should_ImmediatelyUpdateTheVisibleLightbulb_WithoutANewTextChangeOrCaretMove()
    {
        var settings = new CSharpLanguageSettings();
        var prefs = MakePrefs(settings);
        var intelliSense = new Mock<IIntelliSenseService>();
        var (_, editor, margin) = CreateControl(prefs, intelliSense);

        editor.Text = "line1\n";
        intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text,
            (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(1, "Add using System;", DiagnosticSeverity.Warning, "fix-key")]);

        for (var i = 0; i < 100 && margin.Fixes.Count == 0; i++)
            await Task.Delay(20);
        Assert.NotEmpty(margin.Fixes);

        // Flip the setting and raise the same change notification EditorTabItemControl.SetupEditor
        // subscribes to (IPreferencesService.Changed) — no new text/caret-move event at all.
        settings.CodeFixesEnabled = false;
        prefs.Raise(p => p.Changed += null);

        for (var i = 0; i < 100 && margin.Fixes.Count != 0; i++)
            await Task.Delay(20);

        Assert.Empty(margin.Fixes);
    }
}
