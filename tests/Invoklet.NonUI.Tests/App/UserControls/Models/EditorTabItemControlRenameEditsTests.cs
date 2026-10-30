using AvaloniaEdit.Document;
using Invoklet.Contracts.IntelliSense;
using Invoklet.UserControls;
using Xunit;

namespace Invoklet.NonUI.Tests.App.UserControls.Models;

// EditorsTabItemControl.ApplyRenameEdits is the actual
// edit-application logic behind F2 rename, split out specifically so it's testable against a plain
// TextDocument without a real modal RenameSymbolDialog in the loop (there's no existing seam to
// stub ShowDialog in this codebase — see ShowGoToLine/GoToLineDialog, which has no test coverage
// for the same reason). Symbol resolution itself (which spans to replace) is covered separately by
// CSharpIntelliSenseRenameTests.cs.
public class EditorTabItemControlRenameEditsTests
{
    private sealed record TestSpan(int Start, int Length) : IDocumentHighlightSpan;

    [Fact]
    public void ApplyRenameEdits_Should_ReplaceEveryOccurrence_WithTheNewName()
    {
        var document = new TextDocument("value = 1;\nConsole.WriteLine(value);");
        // "value" at offset 0 (length 5) and at offset 29 (inside WriteLine(...)).
        var secondOccurrence = document.Text.IndexOf("value)", StringComparison.Ordinal);
        IReadOnlyList<IDocumentHighlightSpan> spans = [new TestSpan(0, 5), new TestSpan(secondOccurrence, 5)];

        EditorsTabItemControl.ApplyRenameEdits(document, spans, "count");

        Assert.Equal("count = 1;\nConsole.WriteLine(count);", document.Text);
    }

    // The whole reason this method sorts descending instead of just iterating the spans as given —
    // replacing the FIRST (lowest-offset) occurrence first, when the new name is a different length
    // than the old one, shifts every later offset out from under the still-to-come Document.Replace
    // calls (Document.Replace doesn't know about or adjust for sibling calls). Using a name longer
    // than the original ("value" -> "counter") makes this failure mode actually observable — same-
    // length replacements would silently pass either way.
    [Fact]
    public void ApplyRenameEdits_Should_NotCorruptLaterOffsets_WhenTheNewNameIsALongerString()
    {
        var document = new TextDocument("value + value + value");
        IReadOnlyList<IDocumentHighlightSpan> spans =
        [
            new TestSpan(0, 5),
            new TestSpan(8, 5),
            new TestSpan(16, 5),
        ];

        EditorsTabItemControl.ApplyRenameEdits(document, spans, "counter");

        Assert.Equal("counter + counter + counter", document.Text);
    }

    [Fact]
    public void ApplyRenameEdits_Should_GroupEverything_IntoASingleUndoStep()
    {
        var document = new TextDocument("value + value");
        IReadOnlyList<IDocumentHighlightSpan> spans = [new TestSpan(0, 5), new TestSpan(8, 5)];

        EditorsTabItemControl.ApplyRenameEdits(document, spans, "count");
        Assert.Equal("count + count", document.Text);

        document.UndoStack.Undo();

        Assert.Equal("value + value", document.Text);
    }
}
