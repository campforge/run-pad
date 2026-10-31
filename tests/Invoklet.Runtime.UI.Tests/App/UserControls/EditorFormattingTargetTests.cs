using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;
using Invoklet.UserControls;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// The editor as the formatting sees it: the selection or the caret, a version that moves with every change, and the edits applied as one
// change that one undo takes back.
public class EditorFormattingTargetTests
{
    private static CodeTextEditor Editor(string text) => new() { Document = new TextDocument(text) };

    [AvaloniaFact]
    public void Selection_NothingSelected_IsAZeroLengthSelectionAtTheCaret()
    {
        var editor = Editor("var a = 1;");
        editor.TextArea.Caret.Offset = 4;

        Assert.Equal((4, 0), new EditorFormattingTarget(editor).Selection);
    }

    [AvaloniaFact]
    public void Selection_ATextSelected_IsItsStartAndLength()
    {
        var editor = Editor("var a = 1;");
        editor.Select(4, 5);

        Assert.Equal((4, 5), new EditorFormattingTarget(editor).Selection);
    }

    [AvaloniaFact]
    public void IsUnchangedSince_AnEditAfterTheVersionWasTaken_IsFalse()
    {
        var editor = Editor("var a = 1;");
        var target = new EditorFormattingTarget(editor);
        var version = target.CaptureVersion();
        var before = target.IsUnchangedSince(version);

        editor.Document.Insert(0, " ");

        Assert.True(before);
        Assert.False(target.IsUnchangedSince(version));
    }

    [AvaloniaFact]
    public void IsUnchangedSince_TheDocumentReplaced_IsFalse()
    {
        var editor = Editor("var a = 1;");
        var target = new EditorFormattingTarget(editor);
        var version = target.CaptureVersion();

        editor.Document = new TextDocument("var a = 1;");

        Assert.False(target.IsUnchangedSince(version));
    }

    [AvaloniaFact]
    public void Apply_SeveralEdits_ChangeTheTextAndOneUndoTakesThemAllBack()
    {
        var editor = Editor("var a=1;\nvar b=2;");
        var target = new EditorFormattingTarget(editor);

        target.Apply([new TextEdit(5, 0, " "), new TextEdit(6, 0, " "), new TextEdit(14, 0, " "), new TextEdit(15, 0, " ")]);
        var formatted = editor.Text;
        editor.Document.UndoStack.Undo();

        Assert.Equal("var a = 1;\nvar b = 2;", formatted);
        Assert.Equal("var a=1;\nvar b=2;", editor.Text);
    }

    [AvaloniaFact]
    public void Apply_EditsGivenOutOfOrder_StillLandWhereTheyBelong()
    {
        var editor = Editor("ab");
        var target = new EditorFormattingTarget(editor);

        target.Apply([new TextEdit(2, 0, "!"), new TextEdit(0, 0, ">")]);

        Assert.Equal(">ab!", editor.Text);
    }
}
