using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Matches by DiagnosticId, not Title:
// Preferences → C# → "code fixes enabled" master switch and the
// excluded-diagnostic-id list, both filtering the lightbulb margin's merged diagnostic-fix +
// refactoring list. Driven through a REAL EditorsTabItemControl against a MOCKED
// IIntelliSenseService (same idiom as EditorTabItemControlRefactoringsTests), reading
// CodeFixMargin.Fixes directly to check exactly which entries survived filtering, not just which
// line has *a* fix (WorstCodeFixSeverityOnLine can't tell two same-severity candidates on different
// lines apart, but this needs to: it's asserting on WHICH one got excluded).
public class EditorTabItemControlCodeFixSettingsTests
{
    private sealed record FakeCodeFixInfo(int Line, string Title, DiagnosticSeverity Severity, string Key, string? DiagnosticId = null) : ICodeFixInfo;

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
        => CreateControl(prefs, intelliSense.Object);

    private static (EditorsTabItemControl ctrl, CodeTextEditor editor, CodeFixMargin margin) CreateControl(
        Mock<IPreferencesService> prefs, IIntelliSenseService intelliSense)
    {
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense, new CSharpReferenceService(), buildCache.Object);
        var host = new Window { Content = ctrl };
        host.Show();

        var editor = ctrl.State.Editor;
        var margin = editor.TextArea.LeftMargins.OfType<CodeFixMargin>().Single();
        return (ctrl, editor, margin);
    }

    private static CSharpIntelliSenseService CreateRealIntelliSense()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [AvaloniaFact]
    public async Task ExcludedDiagnosticIds_Should_HideOnlyTheMatchingEntry_KeepingOthersVisible()
    {
        var settings = new CSharpLanguageSettings { ExcludedDiagnosticIds = ["ide0007"] }; // lowercase on purpose
        var prefs = MakePrefs(settings);
        var intelliSense = new Mock<IIntelliSenseService>();
        var (_, editor, margin) = CreateControl(prefs, intelliSense);

        editor.Text = "line1\nline2\n";

        intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text, (IReadOnlyList<ICodeFixInfo>)
        [
            new FakeCodeFixInfo(1, "Use explicit type instead of 'var'", DiagnosticSeverity.Info, "excluded-key", "IDE0007"),
            new FakeCodeFixInfo(2, "Add using System;", DiagnosticSeverity.Warning, "kept-key", "CS0246"),
        ]);

        await TestWait.WhileAsync(() => margin.Fixes.Count == 0);

        var keys = margin.Fixes.Select(f => f.Key).ToList();
        Assert.DoesNotContain("excluded-key", keys);
        Assert.Contains("kept-key", keys);
    }

    // A refactoring has no underlying diagnostic (CodeFixEntry.DiagnosticId is null for it — see its
    // own doc comment), so it can never match an excluded ID — accepted, documented narrowing versus
    // the title-substring filter this replaced (UpdateLightbulb's own doc comment covers why).
    [AvaloniaFact]
    public async Task ExcludedDiagnosticIds_Should_NeverMatch_ARefactoringEntry_SinceItHasNoDiagnosticId()
    {
        var settings = new CSharpLanguageSettings { ExcludedDiagnosticIds = ["IDE0007"] };
        var prefs = MakePrefs(settings);
        var intelliSense = new Mock<IIntelliSenseService>();
        intelliSense
            .Setup(s => s.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ICodeFixInfo>?)[new FakeCodeFixInfo(1, "Use expression body", DiagnosticSeverity.Info, "refactor-key")]);
        var (_, editor, margin) = CreateControl(prefs, intelliSense);

        editor.Text = "line1\n";
        editor.TextArea.Caret.Offset = 1;

        await TestWait.WhileAsync(() => margin.Fixes.Count == 0);

        Assert.Contains(margin.Fixes, f => f.Key == "refactor-key");
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
            (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(2, "Add using System;", DiagnosticSeverity.Warning, "fix-key", "CS0246")]);
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
            (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(1, "Add using System;", DiagnosticSeverity.Warning, "fix-key", "CS0246")]);

        await TestWait.WhileAsync(() => margin.Fixes.Count == 0);
        Assert.NotEmpty(margin.Fixes);

        // Flip the setting and raise the same change notification EditorTabItemControl.SetupEditor
        // subscribes to (IPreferencesService.Changed) — no new text/caret-move event at all.
        settings.CodeFixesEnabled = false;
        prefs.Raise(p => p.Changed += null);

        await TestWait.WhileAsync(() => margin.Fixes.Count != 0);

        Assert.Empty(margin.Fixes);
    }

    // User request 2026-07-21 (pass 106) — real end-to-end proof, not just the unit-level
    // "default List<string> contains IDE0055" check (PreferencesWindowViewModelTests.
    // CSharpLanguageSettings_Should_ExcludeFixFormatting_ByDefault): a REAL CSharpIntelliSenseService
    // (Roslyn's own FormattingAnalyzer, not a fake) run against deliberately badly-formatted code,
    // through the whole real pipeline, with completely default settings (no test ever sets
    // ExcludedDiagnosticIds here) — confirms the default value actually suppresses the real
    // suggestion in practice, not merely in the settings object.
    [AvaloniaFact]
    public async Task FixFormatting_Should_NotAppear_ByDefault_ForRealBadlyFormattedCode()
    {
        var prefs = MakePrefs();
        using var intelliSense = CreateRealIntelliSense();

        // Deliberately misindented/misspaced — real Microsoft.CodeAnalysis.CSharp.Features
        // FormattingAnalyzer (IDE0055, "Fix formatting") fires on exactly this kind of deviation.
        const string code = "class C\n{\n    void M(  )\n    {\n            int x=1;\n    }\n}\n";

        // Waits specifically for the CodeFixesChanged firing that matches THIS code (the
        // diagnostic-driven half), rather than polling margin.Fixes for "any entry": a
        // caret-position-triggered refactoring (Generate constructor, Add 'DebuggerDisplay' attribute, ...) can
        // and does arrive first for this same code/caret, and an earlier CodeFixesChanged firing for
        // the control's initial empty document would also satisfy a same-event-but-wrong-code check —
        // either would race a false pass before the diagnostic-fix pass this test actually cares
        // about has run against the REAL code at all.
        var codeFixesChangedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        intelliSense.CodeFixesChanged += (c, _) =>
        {
            if (c == code) codeFixesChangedTcs.TrySetResult();
        };

        var (_, editor, margin) = CreateControl(prefs, intelliSense);
        editor.Text = code;

        await codeFixesChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // CodeFixesChanged's own handler still needs its own Dispatcher.UIThread.Post to reach
        // margin.Update — waiting for margin.Fixes to merely become non-empty isn't enough: the
        // caret-position-triggered refactorings (DiagnosticId null, see CodeFixEntry's own doc
        // comment) reliably render FIRST for this code/caret and already make the list non-empty
        // before the diagnostic-fix half's own Post has run, which would read a stale, still-pending
        // margin (confirmed by temporarily logging the raw CodeFixesChanged payload: it does contain
        // "Fix formatting"/IDE0055 among CS0219/IDE0040/IDE0059/IDE0051, all present at the same
        // time margin.Fixes still only showed refactorings). Wait specifically for a
        // DIAGNOSTIC-driven entry (non-null DiagnosticId) instead — the other, non-IDE0055 fixes
        // always survive filtering by default, so this condition is reachable regardless of whether
        // the exclusion under test is even working.
        await TestWait.WhileAsync(() => !margin.Fixes.Any(f => f.DiagnosticId is not null));

        Assert.DoesNotContain(margin.Fixes, f => f.Title.Contains("formatting", StringComparison.OrdinalIgnoreCase));
    }
}
