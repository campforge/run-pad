using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.CodeTextEditor.Highlighters;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeTextEditor;

// Editor smoothness (user report: typing felt laggy and the editor flickered). Background
// decorations — semantic colors, inlay hints, squiggles, occurrence highlights — used to sit on the
// absolute offsets of the text they were computed for, so every keystroke put them on the wrong
// characters until the next analysis repainted everything. They now follow the document's edits, and
// an analysis result redraws only the span whose decorations actually changed.
public class EditorDecorationTrackingTests
{
    private sealed record Token(int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;
    private sealed record Hint(int Offset, string Text) : IInlayHint;
    private sealed record Diagnostic(int StartOffset, int EndOffset, string Message, DiagnosticSeverity Severity) : IDiagnosticInfo;

    // ── what follows the edits ──

    [AvaloniaFact]
    public void SemanticTokens_Should_StayOnTheirWords_WhenTextIsTypedBeforeThem()
    {
        var document = new TextDocument("int a; int b;");
        var highlighter = new SemanticHighlighter();
        highlighter.Update(document, [new Token(4, 1, SemanticTokenKind.LocalName), new Token(11, 1, SemanticTokenKind.LocalName)]);

        document.Insert(0, "// typed\n");

        Assert.Equal(["a", "b"], highlighter.Tokens.Select(t => document.GetText(t)));
    }

    [AvaloniaFact]
    public void InlayHints_Should_StayInFrontOfTheirIdentifier_WhenTextIsTypedBeforeIt()
    {
        var document = new TextDocument("var x = 5;");
        var renderer = new InlayHintRenderer();
        renderer.Update(document, [new Hint(4, "int ")]);

        document.Insert(0, "  ");

        var hint = Assert.Single(renderer.Hints);
        Assert.Equal("x", document.GetText(hint.StartOffset, 1));
        Assert.Equal(6, renderer.GetFirstInterestedOffset(0));
    }

    [AvaloniaFact]
    public void Squiggles_AndTheirHoverInfo_Should_StayUnderTheirCode_WhenTextIsTypedBeforeIt()
    {
        var document = new TextDocument("var y = undefinedName;");
        var renderer = new SquiggleRenderer();
        renderer.Update(document, [new Diagnostic(8, 21, "CS0103", DiagnosticSeverity.Error)]);

        document.Insert(0, "\n\n");

        var diagnostic = Assert.Single(renderer.Diagnostics);
        Assert.Equal("undefinedName", document.GetText(diagnostic.StartOffset, diagnostic.EndOffset - diagnostic.StartOffset));
        Assert.Equal("CS0103", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [AvaloniaFact]
    public void OccurrenceHighlights_Should_StayOnTheirWords_WhenTextIsTypedBeforeThem()
    {
        var document = new TextDocument("x = x + 1;");
        var highlighter = new OccurrenceHighlighter();
        highlighter.Update(document, [(0, 1), (4, 1)]);

        document.Insert(0, "var ");

        Assert.Equal([(4, 1), (8, 1)], highlighter.Spans);
    }

    [AvaloniaFact]
    public void Decorations_Should_BeDroppedAndClipped_WhenTheyDoNotFitTheDocument()
    {
        var document = new TextDocument("abc");
        var highlighter = new SemanticHighlighter();

        highlighter.Update(document, [new Token(1, 10, SemanticTokenKind.LocalName), new Token(7, 1, SemanticTokenKind.LocalName)]);

        var token = Assert.Single(highlighter.Tokens);
        Assert.Equal((1, 3), (token.StartOffset, token.EndOffset));
    }

    // ── what a result redraws ──

    [AvaloniaFact]
    public void SemanticUpdate_Should_ReportOnlyTheSpanThatChanged_AndNothingWhenNothingDid()
    {
        var document = new TextDocument("int a; int b; int c;");
        var highlighter = new SemanticHighlighter();
        ISemanticToken[] tokens =
        [
            new Token(4, 1, SemanticTokenKind.LocalName), new Token(11, 1, SemanticTokenKind.LocalName),
            new Token(18, 1, SemanticTokenKind.LocalName),
        ];
        Assert.Equal((4, 19), highlighter.Update(document, tokens)); // first update: all of them

        Assert.Null(highlighter.Update(document, tokens)); // the same result: no redraw at all
        Assert.Equal((11, 12), highlighter.Update(document,
            [tokens[0], new Token(11, 1, SemanticTokenKind.FieldName), tokens[2]])); // only b's kind changed
    }

    [AvaloniaFact]
    public void InlayUpdate_Should_ReportOnlyTheHintsThatChanged()
    {
        var document = new TextDocument("var x = 5; var y = 6;");
        var renderer = new InlayHintRenderer();
        renderer.Update(document, [new Hint(4, "int "), new Hint(15, "int ")]);

        Assert.Null(renderer.Update(document, [new Hint(4, "int "), new Hint(15, "int ")]));
        Assert.Equal((15, 15), renderer.Update(document, [new Hint(4, "int "), new Hint(15, "long ")]));
    }

    [Theory]
    [InlineData(new[] { 0, 5, 10 }, new[] { 0, 5, 10 }, null, null)]
    [InlineData(new[] { 0, 5, 10 }, new[] { 0, 6, 10 }, 5, 7)]      // one moved
    [InlineData(new[] { 0, 10 }, new[] { 0, 5, 10 }, 5, 6)]          // one inserted
    [InlineData(new[] { 0, 5, 10 }, new[] { 0, 10 }, 5, 6)]          // one removed
    [InlineData(new int[0], new[] { 3, 7 }, 3, 8)]                   // from nothing
    public void ChangedRange_Should_CoverOnlyTheDifferingMiddle(int[] before, int[] after, int? start, int? end)
    {
        static List<TextSegment> Segments(int[] starts) => [.. starts.Select(s => new TextSegment { StartOffset = s, Length = 1 })];

        var range = TrackedSegments<TextSegment>.ChangedRange(Segments(before), Segments(after), (_, _) => true);

        Assert.Equal(start is null ? null : (start.Value, end!.Value), range);
    }

    // ── end to end: a real editor with real Roslyn analysis ──

    [AvaloniaFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Roslyn")]
    public async Task InARealEditor_SemanticColors_Should_FollowTyping_AndAResultShould_RedrawOnlyWhatChanged()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var references = new CSharpReferenceService();
        using var intelliSense = new CSharpIntelliSenseService(
            references.GetDefault().Append(references.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance);
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var control = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense, new Mock<IBuildCacheService>().Object);
        new Window { Content = control }.Show();
        var editor = control.State.Editor;
        var highlighter = control.SemanticHighlighterForTests!;

        editor.Text = "var first = 1;\nvar second = first + 1;\nSystem.Console.WriteLine(second);";
        await TestWait.WhileAsync(() => highlighter.Tokens.Count == 0 || control.RedrawsRun == 0);
        var words = highlighter.Tokens.Select(t => editor.Document.GetText(t)).ToList();
        Assert.Contains("first", words);
        Assert.Contains("second", words);

        // typed before everything: until the next analysis, every color is still on its own word
        editor.Document.Insert(0, "// a comment typed at the top\n");
        Assert.Equal(words, highlighter.Tokens.Select(t => editor.Document.GetText(t)));
    }
}
