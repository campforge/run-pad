using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;

namespace Invoklet.UserControls;

/// <summary>A code editor as <see cref="EditorFormatting"/> sees it.</summary>
internal sealed class EditorFormattingTarget(CodeTextEditor editor) : IFormattingTarget
{
    public string Text => editor.Text;

    public int CaretOffset => editor.TextArea.Caret.Offset;

    public (int Start, int Length) Selection
        => editor.TextArea.Selection.IsEmpty
            ? (CaretOffset, 0)
            : (editor.TextArea.Selection.SurroundingSegment.Offset, editor.TextArea.Selection.SurroundingSegment.Length);

    // The document and its version: either changing (an edit, or the document replaced) means the answer was made for other text.
    public object CaptureVersion() => (editor.Document, editor.Document?.Version);

    public bool IsUnchangedSince(object version)
        => version is (object document, object docVersion)
            && ReferenceEquals(editor.Document, document) && ReferenceEquals(editor.Document?.Version, docVersion);

    public void Apply(IReadOnlyList<TextEdit> edits)
    {
        var document = editor.Document;
        if (document is null) return;
        // One undo step; from the end so that the earlier offsets stay where they are.
        using (document.RunUpdate())
        {
            foreach (var edit in edits.OrderByDescending(e => e.Start))
                document.Replace(edit.Start, edit.Length, edit.NewText);
        }
    }
}
