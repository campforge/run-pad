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

// TODO.md #74 "CodeRefactoringProvider хостинг в лампочке" — the lightbulb margin now merges two
// independent sources (EditorTabItemControl.SetupEditor's own doc comment has the full rationale):
// diagnostic-driven fixes (CodeFixesChanged, pushed on text change) and caret-position-driven
// refactorings (GetRefactoringsAsync, pulled on caret move, debounced). These tests drive a REAL
// EditorsTabItemControl against a MOCKED IIntelliSenseService — raising CodeFixesChanged directly and
// stubbing GetRefactoringsAsync — to prove neither source clobbers the other's entries in the shared
// margin, using CodeTextEditor.WorstCodeFixSeverityOnLine as the observability hook (no need to
// simulate a real paint pass or menu click to know what UpdateCodeFixes was actually given).
public class EditorTabItemControlRefactoringsTests
{
    private sealed record FakeCodeFixInfo(int Line, string Title, DiagnosticSeverity Severity, string Key) : ICodeFixInfo;

    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
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

    [AvaloniaFact]
    public async Task LightbulbMargin_Should_ShowBothDiagnosticFixesAndRefactorings_Simultaneously()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(null, [], prefs.Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();
        var intelliSense = new Mock<IIntelliSenseService>();

        // Line 5 will host the refactoring — GetRefactoringsAsync is stubbed independently of what
        // text/offset it's called with, since the exact caret offset the debounce lands on isn't the
        // point of this test.
        intelliSense
            .Setup(s => s.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ICodeFixInfo>?)[new FakeCodeFixInfo(5, "Use expression body", DiagnosticSeverity.Info, "refactor-key")]);

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense.Object, new CSharpReferenceService(), buildCache.Object, editorBackground: null);
        var host = new Window { Content = ctrl };
        host.Show();

        var editor = ctrl.State.Editor;
        editor.Text = "line1\nline2\nline3\nline4\nline5\n";

        // Diagnostic-driven fix on line 2 — CodeFixesChanged fires with the code it was computed
        // against, same "still matches editor.Text" guard SetupEditor's own handler checks. The
        // handler applies it via Dispatcher.UIThread.Post, so it isn't visible synchronously — same
        // poll idiom as the refactoring wait below.
        intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text,
            (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(2, "Add using", DiagnosticSeverity.Warning, "fix-key")]);

        for (var i = 0; i < 100 && editor.WorstCodeFixSeverityOnLine(2) is null; i++)
            await Task.Delay(20);

        Assert.Equal(CodeFixSeverity.Warning, editor.WorstCodeFixSeverityOnLine(2));
        Assert.Null(editor.WorstCodeFixSeverityOnLine(5)); // refactoring hasn't been fetched yet

        // Caret move — triggers the debounced GetRefactoringsAsync fetch.
        editor.TextArea.Caret.Offset = 3;

        for (var i = 0; i < 100 && editor.WorstCodeFixSeverityOnLine(5) is null; i++)
            await Task.Delay(20);

        // Both present at once: the refactoring didn't clobber the still-valid diagnostic fix, and
        // vice versa — the whole point of tracking the two sources independently.
        Assert.Equal(CodeFixSeverity.Info, editor.WorstCodeFixSeverityOnLine(5));
        Assert.Equal(CodeFixSeverity.Warning, editor.WorstCodeFixSeverityOnLine(2));
    }
}
