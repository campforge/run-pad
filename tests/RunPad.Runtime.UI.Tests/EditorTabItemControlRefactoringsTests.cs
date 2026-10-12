using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 "CodeRefactoringProvider hosting in the lightbulb" — the lightbulb margin now merges two
// independent sources (EditorTabItemControl.SetupEditor's own doc comment has the full rationale):
// diagnostic-driven fixes (CodeFixesChanged, pushed on text change) and caret-position-driven
// refactorings (GetRefactoringsAsync, pulled on caret move, debounced). These tests drive a REAL
// EditorsTabItemControl against a MOCKED IIntelliSenseService — raising CodeFixesChanged directly and
// stubbing GetRefactoringsAsync — to prove neither source clobbers the other's entries in the shared
// margin, using CodeTextEditor.WorstCodeFixSeverityOnLine as the observability hook (no need to
// simulate a real paint pass or menu click to know what UpdateCodeFixes was actually given).
public class EditorTabItemControlRefactoringsTests
{
    private sealed record FakeCodeFixInfo(int Line, string Title, DiagnosticSeverity Severity, string Key, string? DiagnosticId = null) : ICodeFixInfo;

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
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();
        var intelliSense = new Mock<IIntelliSenseService>();

        // Every GetRefactoringsAsync call stays pending until the test completes it explicitly. An
        // immediately-completed stub raced the diagnostic assertions on slow CI (Linux run
        // 38284374454): the initial Text assignment can move the caret and schedule a debounced fetch
        // that lands its line-5 refactoring before the "not fetched yet" check. A superseded call's
        // token is cancelled by the next caret move, so its pending task is cancelled the same way a
        // real Roslyn call would be.
        var refactoringCalls = new List<(int Offset, CancellationToken Token, TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?> Result)>();
        intelliSense
            .Setup(s => s.GetRefactoringsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, int offset, CancellationToken ct) =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                lock (refactoringCalls)
                    refactoringCalls.Add((offset, ct, tcs));
                return tcs.Task;
            });

        TaskCompletionSource<IReadOnlyList<ICodeFixInfo>?>? PendingCallAt(int offset)
        {
            lock (refactoringCalls)
                return refactoringCalls.LastOrDefault(c => c.Offset == offset && !c.Token.IsCancellationRequested).Result;
        }

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense.Object, new CSharpReferenceService(), buildCache.Object);
        var host = new Window { Content = ctrl };
        try
        {
            host.Show();

            var editor = ctrl.State.Editor;
            editor.Text = "line1\nline2\nline3\nline4\nline5\n";

            // Diagnostic-driven fix on line 2 — CodeFixesChanged fires with the code it was computed
            // against, same "still matches editor.Text" guard SetupEditor's own handler checks. The
            // handler applies it via Dispatcher.UIThread.Post, so it isn't visible synchronously — same
            // poll idiom as the refactoring wait below.
            intelliSense.Raise(s => s.CodeFixesChanged += null, editor.Text,
                (IReadOnlyList<ICodeFixInfo>)[new FakeCodeFixInfo(2, "Add using", DiagnosticSeverity.Warning, "fix-key")]);

            await TestWait.WhileAsync(() => editor.WorstCodeFixSeverityOnLine(2) is null);

            Assert.Equal(CodeFixSeverity.Warning, editor.WorstCodeFixSeverityOnLine(2));
            Assert.Null(editor.WorstCodeFixSeverityOnLine(5)); // no refactoring response has completed

            // Caret move — triggers the debounced GetRefactoringsAsync fetch, which stays pending.
            editor.TextArea.Caret.Offset = 3;

            await TestWait.WhileAsync(() => PendingCallAt(3) is null);
            var pending = PendingCallAt(3);
            Assert.NotNull(pending);

            // While the refactoring response is withheld, the diagnostic fix is still shown alone.
            Assert.Equal(CodeFixSeverity.Warning, editor.WorstCodeFixSeverityOnLine(2));
            Assert.Null(editor.WorstCodeFixSeverityOnLine(5));

            pending.SetResult([new FakeCodeFixInfo(5, "Use expression body", DiagnosticSeverity.Info, "refactor-key")]);

            await TestWait.WhileAsync(() => editor.WorstCodeFixSeverityOnLine(5) is null);

            // Both present at once: the refactoring didn't clobber the still-valid diagnostic fix, and
            // vice versa — the whole point of tracking the two sources independently.
            Assert.Equal(CodeFixSeverity.Info, editor.WorstCodeFixSeverityOnLine(5));
            Assert.Equal(CodeFixSeverity.Warning, editor.WorstCodeFixSeverityOnLine(2));
        }
        finally
        {
            host.Close();
            ctrl.Dispose();
            vm.Dispose();
        }
    }
}
