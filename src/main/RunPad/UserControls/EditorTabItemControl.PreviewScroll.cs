using Avalonia.Controls;
using Avalonia.Interactivity;
using RunPad.UserControls.Models;

namespace RunPad.UserControls;

// Side by side, the editor and the preview scroll together, as in VS Code: the pane the user works
// in leads, and the other one follows the source line at its top. Lines are counted from zero, with
// the fraction of the top line scrolled past.
public partial class EditorsTabItemControl
{
    private enum ScrollLeader { Editor, Preview }

    private ScrollLeader _scrollLeader;
    // The line of the preview's Markdown where the editor's first line is: the header table and the
    // text before the code come first.
    private int _previewCodeLine;

    private void SetupPreviewScrollSync()
    {
        State.Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrolledForPreview;
        LeadScrollingFrom(EditorHost, ScrollLeader.Editor);
        LeadScrollingFrom(PreviewHost, ScrollLeader.Preview);
    }

    private void DisposePreviewScrollSync()
        => State.Editor.TextArea.TextView.ScrollOffsetChanged -= OnEditorScrolledForPreview;

    // Only the leader's scrolling is followed, so the follower's own scroll events, which come back
    // after it was moved, do not move the leader in turn.
    private void LeadScrollingFrom(Control pane, ScrollLeader leader)
    {
        pane.AddHandler(PointerWheelChangedEvent, (_, _) => _scrollLeader = leader, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(PointerPressedEvent, (_, _) => _scrollLeader = leader, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(KeyDownEvent, (_, _) => _scrollLeader = leader, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private bool ScrollSyncActive(ScrollLeader from)
        => _preview is not null && _vm.PreviewMode == ScriptPreviewMode.Side && _scrollLeader == from;

    private void OnEditorScrolledForPreview(object? sender, EventArgs e)
    {
        if (ScrollSyncActive(ScrollLeader.Editor)) _preview!.ScrollToSourceLine(PreviewLineFor(EditorTopLine()));
    }

    private void OnPreviewScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y != 0 && ScrollSyncActive(ScrollLeader.Preview) && _preview!.GetSourceLineAtTop() is { } line)
            ScrollEditorToLine(EditorLineFor(line));
    }

    // The editor's first line stretches over everything before the code in the preview, so that the
    // top of the editor shows the top of the document and scrolling stays continuous both ways.
    private double PreviewLineFor(double editorLine)
        => editorLine < 1 ? editorLine * (_previewCodeLine + 1) : _previewCodeLine + editorLine;

    private double EditorLineFor(double previewLine)
        => previewLine < _previewCodeLine + 1 ? previewLine / (_previewCodeLine + 1) : previewLine - _previewCodeLine;

    // Folded lines take no height, so lines are found by their visual top.
    private double EditorTopLine()
    {
        var textView = State.Editor.TextArea.TextView;
        var y = textView.VerticalOffset;
        if (textView.GetDocumentLineByVisualTop(y) is not { } line) return 0;
        var (top, bottom) = EditorLineBounds(line.LineNumber);
        return line.LineNumber - 1 + (bottom > top ? Math.Clamp((y - top) / (bottom - top), 0, 1) : 0);
    }

    private void ScrollEditorToLine(double editorLine)
    {
        var lineNumber = Math.Clamp((int)Math.Floor(editorLine) + 1, 1, State.Editor.Document.LineCount);
        var (top, bottom) = EditorLineBounds(lineNumber);
        State.Editor.ScrollToVerticalOffset(top + Math.Clamp(editorLine - (lineNumber - 1), 0, 1) * (bottom - top));
    }

    private (double Top, double Bottom) EditorLineBounds(int lineNumber)
    {
        var textView = State.Editor.TextArea.TextView;
        var top = textView.GetVisualTopByDocumentLine(lineNumber);
        return (top, lineNumber < State.Editor.Document.LineCount
            ? textView.GetVisualTopByDocumentLine(lineNumber + 1)
            : top + textView.DefaultLineHeight);
    }
}
