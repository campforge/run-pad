using AvaloniaEdit.Document;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Avalonia.CodeTextEditor.Breakpoints;

namespace Invoklet.UserControls;

internal sealed class EditorInteractionSubscriptions : IDisposable
{
    private readonly CodeTextEditor _editor;
    private readonly EventHandler _textChanged;
    private readonly EventHandler _caretPositionChanged;
    private readonly EventHandler<DocumentChangedEventArgs> _documentChanged;
    private readonly EventHandler _renameRequested;
    private readonly EventHandler _findReferencesRequested;
    private readonly EventHandler _goToDefinitionRequested;
    private readonly Action<CodeFixEntry> _fixApplyRequested;
    private readonly EventHandler<BreakpointEventArgs> _breakpointAdded;
    private readonly EventHandler<BreakpointEventArgs> _breakpointRemoved;
    private readonly Action<int, IBreakpoint?> _breakpointRightClicked;
    private bool _attached;

    public EditorInteractionSubscriptions(CodeTextEditor editor,
        EventHandler textChanged, EventHandler caretPositionChanged, EventHandler<DocumentChangedEventArgs> documentChanged,
        EventHandler renameRequested, EventHandler findReferencesRequested,
        EventHandler goToDefinitionRequested, Action<CodeFixEntry> fixApplyRequested,
        EventHandler<BreakpointEventArgs> breakpointAdded, EventHandler<BreakpointEventArgs> breakpointRemoved,
        Action<int, IBreakpoint?> breakpointRightClicked)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _textChanged = textChanged ?? throw new ArgumentNullException(nameof(textChanged));
        _caretPositionChanged = caretPositionChanged ?? throw new ArgumentNullException(nameof(caretPositionChanged));
        _documentChanged = documentChanged ?? throw new ArgumentNullException(nameof(documentChanged));
        _renameRequested = renameRequested ?? throw new ArgumentNullException(nameof(renameRequested));
        _findReferencesRequested = findReferencesRequested ?? throw new ArgumentNullException(nameof(findReferencesRequested));
        _goToDefinitionRequested = goToDefinitionRequested ?? throw new ArgumentNullException(nameof(goToDefinitionRequested));
        _fixApplyRequested = fixApplyRequested ?? throw new ArgumentNullException(nameof(fixApplyRequested));
        _breakpointAdded = breakpointAdded ?? throw new ArgumentNullException(nameof(breakpointAdded));
        _breakpointRemoved = breakpointRemoved ?? throw new ArgumentNullException(nameof(breakpointRemoved));
        _breakpointRightClicked = breakpointRightClicked ?? throw new ArgumentNullException(nameof(breakpointRightClicked));
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _editor.TextChanged += _textChanged;
        _editor.TextArea.Caret.PositionChanged += _caretPositionChanged;
        _editor.DocumentChanged += _documentChanged;
        _editor.RenameSymbolRequested += _renameRequested;
        _editor.FindReferencesRequested += _findReferencesRequested;
        _editor.GoToDefinitionRequested += _goToDefinitionRequested;
        _editor.FixApplyRequested += _fixApplyRequested;
        _editor.BreakpointAdded += _breakpointAdded;
        _editor.BreakpointRemoved += _breakpointRemoved;
        _editor.BreakpointRightClicked += _breakpointRightClicked;
    }

    public void Dispose()
    {
        if (!_attached) return;
        _attached = false;
        _editor.TextChanged -= _textChanged;
        _editor.TextArea.Caret.PositionChanged -= _caretPositionChanged;
        _editor.DocumentChanged -= _documentChanged;
        _editor.RenameSymbolRequested -= _renameRequested;
        _editor.FindReferencesRequested -= _findReferencesRequested;
        _editor.GoToDefinitionRequested -= _goToDefinitionRequested;
        _editor.FixApplyRequested -= _fixApplyRequested;
        _editor.BreakpointAdded -= _breakpointAdded;
        _editor.BreakpointRemoved -= _breakpointRemoved;
        _editor.BreakpointRightClicked -= _breakpointRightClicked;
    }
}
