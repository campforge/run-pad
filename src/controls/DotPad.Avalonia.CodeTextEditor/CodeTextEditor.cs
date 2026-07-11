using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using static AvaloniaEdit.TextMate.TextMate;

namespace DotPad.Avalonia.CodeTextEditor;

public class CodeTextEditor : TextEditor
{
    public static readonly StyledProperty<bool> ShowBreakpointsProperty =
        AvaloniaProperty.Register<CodeTextEditor, bool>(nameof(ShowBreakpoints));

    public static readonly StyledProperty<bool> ShowFoldingProperty =
        AvaloniaProperty.Register<CodeTextEditor, bool>(nameof(ShowFolding));

    static CodeTextEditor()
    {
        ShowBreakpointsProperty.Changed.AddClassHandler<CodeTextEditor>(
            static (editor, e) => editor.OnShowBreakpointsChanged((bool?)e.NewValue));
        ShowFoldingProperty.Changed.AddClassHandler<CodeTextEditor>(
            static (editor, e) => editor.OnShowFoldingChanged((bool?)e.NewValue));
    }

    private readonly BreakpointStore _breakpointStore;
    private readonly BreakPointMargin _breakpointMargin;
    private readonly CodeFixMargin _codeFixMargin;
    private readonly DebugLineHighlighter _debugLineHighlighter;
    private readonly BraceMatchHighlighter _braceMatchHighlighter = new();
    private readonly BraceFoldingStrategy _foldingStrategy = new();
    private readonly DispatcherTimer _foldingTimer;
    private FoldingManager? _foldingManager;
    private VsCodeFoldingMargin? _foldingMargin;
    private bool _isLoaded;
    private CodeTextEditorOptions _editorOptions = new();
    private RegistryOptions? _registryOptions;
    private Installation? _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;


    public event EventHandler<BreakpointEventArgs>? BreakpointAdded;
    public event EventHandler<BreakpointEventArgs>? BreakpointRemoved;

    /// <summary>Fired on right-click in the breakpoint margin. Args: line number, existing breakpoint (or null).</summary>
    public event Action<int, IBreakpoint?>? BreakpointRightClicked;

    internal void RaiseBreakpointRightClicked(int line, IBreakpoint? bp)
        => BreakpointRightClicked?.Invoke(line, bp);

    /// <summary>Sync completion provider. For async (e.g. Roslyn), use <see cref="AsyncCompletionProvider"/> instead.</summary>
    public event EventHandler<CompletionRequestedEventArgs>? CompletionRequested;

    /// <summary>Sync overload provider. For async use, see <see cref="AsyncOverloadProvider"/>.</summary>
    public event EventHandler<OverloadRequestedEventArgs>? OverloadRequested;

    /// <summary>Async completion provider — called instead of <see cref="CompletionRequested"/> when set.</summary>
    public Func<CompletionRequestedEventArgs, Task>? AsyncCompletionProvider { get; set; }

    /// <summary>Async overload provider — called instead of <see cref="OverloadRequested"/> when set.</summary>
    public Func<OverloadRequestedEventArgs, Task>? AsyncOverloadProvider { get; set; }

    public IEnumerable<IBreakpoint> Breakpoints => _breakpointStore;

    public void AddBreakpoint(int line) => _breakpointStore.Add(line);
    public void RemoveBreakpoint(IBreakpoint bp) => _breakpointStore.Remove(bp);

    /// <summary>Redraws the breakpoint margin — call after updating a breakpoint's VerificationState.</summary>
    public void InvalidateBreakpoints() => _breakpointMargin.InvalidateVisual();

    /// <summary>Updates the lightbulb quick-fix gutter (e.g. after a diagnostics pass finds a missing using).</summary>
    public void UpdateCodeFixes(IReadOnlyList<CodeFixEntry> fixes) => _codeFixMargin.Update(fixes);

    /// <summary>Fired when an applied quick fix wants namespaces imported (see CodeFixEntry.UsingsToAdd).</summary>
    public event Action<IReadOnlyList<string>>? UsingsToAddRequested;

    /// <summary>
    /// Whether the merged gutter shows line numbers (not the base <see cref="TextEditor.ShowLineNumbers"/>,
    /// which this control keeps permanently off — see the ctor comment on that assignment).
    /// </summary>
    public bool ShowLineNumberText
    {
        get => _breakpointMargin.ShowLineNumbers;
        set
        {
            if (_breakpointMargin.ShowLineNumbers == value) return;
            _breakpointMargin.ShowLineNumbers = value;
            _breakpointMargin.InvalidateMeasure();
            _breakpointMargin.InvalidateVisual();
        }
    }

    public ElementGenerator InlineControls { get; } = new();

    /// <summary>Characters that open the completion dropdown.</summary>
    public HashSet<char> CompletionTriggers { get; } = [];

    /// <summary>Characters that open the overload insight window.</summary>
    public HashSet<char> OverloadTriggers { get; } = [];

    /// <summary>Characters — beyond letters/digits, which always count — that continue an identifier
    /// rather than starting a new one (e.g. <c>_</c> in C#/VB). Language-specific, so the app layer
    /// configures this the same way it configures <see cref="CompletionTriggers"/>; this control
    /// doesn't assume any particular language's lexical rules. See ShowCompletion's 24/7 auto-invoke.</summary>
    public HashSet<char> IdentifierChars { get; } = [];

    /// <summary>Characters that open the completion dropdown only when they immediately follow an
    /// identifier character — e.g. <c>&lt;</c> for C#'s generic type arguments (<c>List&lt;</c>).
    /// Kept separate from <see cref="CompletionTriggers"/> (which fires unconditionally) because a
    /// bare <c>&lt;</c> is also a comparison operator, and firing on every one would be noisy; a
    /// language whose generics syntax doesn't use this character at all (e.g. VB's <c>(Of ...)</c>)
    /// simply doesn't register anything here.</summary>
    public HashSet<char> ContextualTriggers { get; } = [];

    /// <summary>Words that, followed by a space, open the completion dropdown — e.g. <c>"new"</c> in
    /// C#/VB, to suggest constructible types right where you'd type one. Language-specific (the
    /// keyword spelling, whether it's case-sensitive), so the app layer configures this.</summary>
    public HashSet<string> WordTriggers { get; } = [];

    /// <summary>Matching bracket/brace pairs (open → close) — the full set this language recognizes,
    /// used for brace-match highlighting, Ctrl+] navigation, closing the signature-help insight
    /// window, and type-through on an already-present closing char. Not every language delimits
    /// blocks/calls with the same characters, so this control doesn't assume any pair; the app layer
    /// configures it. See also <see cref="AutoCloseBraces"/> — a pair being recognized here doesn't
    /// mean it should also be auto-inserted on open.</summary>
    public Dictionary<char, char> BracePairs { get; } = [];

    /// <summary>Subset of <see cref="BracePairs"/>' opening characters that should also auto-insert
    /// their closing character when typed. Deliberately not the same set — e.g. C#'s <c>(</c> is
    /// recognized in <see cref="BracePairs"/> (for matching/navigation) but excluded here: opening a
    /// call's parameter list is exactly when signature help is about to show the real parameter list
    /// from any overloads, and an auto-inserted <c>)</c> sitting there fights that instead of helping
    /// (see ANALYSIS.md TODO #51).</summary>
    public HashSet<char> AutoCloseBraces { get; } = [];

    /// <summary>Characters that end a statement, closing the signature-help insight window if it's
    /// still open (e.g. <c>;</c> in C#) — not every language ends statements the same way (VB doesn't
    /// require one at all), so the app layer configures this.</summary>
    public HashSet<char> StatementTerminators { get; } = [];

    /// <summary>Tab-expandable snippets: keyword → template with <c>${0}</c> cursor marker.</summary>
    public Dictionary<string, string> Snippets { get; } = [];

    /// <summary>Icon shown for <see cref="Snippets"/> entries in the completion list (set by the app
    /// layer, which owns kind→icon mapping) — see ShowCompletion.</summary>
    public IImage? SnippetIcon { get; set; }

    public CodeTextEditorOptions EditorOptions
    {
        get => _editorOptions;
        set { _editorOptions = value; ApplyOptions(); }
    }

    public bool ShowBreakpoints
    {
        get => GetValue(ShowBreakpointsProperty);
        set => SetValue(ShowBreakpointsProperty, value);
    }

    public bool ShowFolding
    {
        get => GetValue(ShowFoldingProperty);
        set => SetValue(ShowFoldingProperty, value);
    }

    public int? DebugCurrentLine
    {
        get => _breakpointMargin.DebugLine;
        set
        {
            _breakpointMargin.DebugLine = value;
            _debugLineHighlighter.CurrentLine = value;
            _breakpointMargin.InvalidateVisual();
            TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            if (value is { } line)
                ScrollToLine(line);
        }
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public CodeTextEditor()
    {
        _breakpointStore = new BreakpointStore();
        _breakpointStore.BreakpointAdded += (_, e) => BreakpointAdded?.Invoke(this, e);
        _breakpointStore.BreakpointRemoved += (_, e) => BreakpointRemoved?.Invoke(this, e);

        _breakpointMargin = new BreakPointMargin(this, _breakpointStore);
        _codeFixMargin = new CodeFixMargin(this);
        _codeFixMargin.UsingsToAddRequested += usings => UsingsToAddRequested?.Invoke(usings);
        TextArea.LeftMargins.Add(_codeFixMargin);
        _debugLineHighlighter = new DebugLineHighlighter();

        _foldingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _foldingTimer.Tick += (_, _) => { _foldingTimer.Stop(); UpdateFoldings(); };

        // Remove DeleteLine — conflicts with our line-based workflow
        var deleteLineCommand = TextArea.CommandBindings
            .OfType<RoutedCommandBinding>()
            .FirstOrDefault(x => x.Command == AvaloniaEditCommands.DeleteLine);
        if (deleteLineCommand != null)
            TextArea.CommandBindings.Remove(deleteLineCommand);

        SetupEditContextMenu();

        HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        // Line numbers are rendered by BreakPointMargin itself (merged gutter — see its class
        // comment), not by AvaloniaEdit's own LineNumberMargin.
        ShowLineNumbers = false;

        TextArea.TextView.BackgroundRenderers.Add(_debugLineHighlighter);
        TextArea.TextView.BackgroundRenderers.Add(_braceMatchHighlighter);
        TextArea.Caret.PositionChanged += (_, _) => UpdateBraceMatch();

        Options.AllowToggleOverstrikeMode = true;
        Options.ShowBoxForControlCharacters = true;
        TextArea.RightClickMovesCaret = true;

        TextArea.TextView.LineTransformers.Add(new UnderlineAndStrikeThroughTransformer());
        TextArea.TextView.ElementGenerators.Add(InlineControls);

        TextArea.TextEntering += OnTextEntering;
        TextArea.TextEntered += OnTextEntered;

        TextChanged += (_, _) => { _foldingTimer.Stop(); _foldingTimer.Start(); };

        // FoldingManager.Install requires the document to be in its final state;
        // defer until Loaded so AvaloniaEdit's template has fully applied.
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            if (ShowFolding && _foldingManager is null)
                InstallFolding();
        };

        // Reinstall when the document object itself is replaced (new file opened).
        DocumentChanged += (_, _) =>
        {
            if (!ShowFolding || !_isLoaded) return;
            if (_foldingManager is not null)
                UninstallFolding();
            InstallFolding();
        };

        AddHandler(PointerWheelChangedEvent, OnFontSizeScroll, RoutingStrategies.Bubble, handledEventsToo: true);

        // Intercept Tab (snippet expansion) and Ctrl+] (brace navigation) before
        // AvaloniaEdit's built-in KeyBinding handlers fire.
        TextArea.AddHandler(
            KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && TryExpandSnippet())
                    e.Handled = true;
                else if (e.Key == Key.OemCloseBrackets && e.KeyModifiers == KeyModifiers.Control)
                {
                    JumpToMatchingBrace();
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
                {
                    ShowOverloads('\0');
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
                {
                    ShowCompletion('\0', explicitInvoke: true);
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel);

        ApplyOptions();
    }

    // ApplicationCommands.Copy/Cut/Paste (AvaloniaEdit.RoutedCommand) resolve ICommand.Execute/
    // CanExecute through a process-wide static "last focused element" tracker, not through this
    // TextArea specifically. Opening the ContextMenu moves keyboard focus onto the clicked MenuItem
    // (for arrow-key navigation), which lives in a disconnected popup visual tree — so binding
    // MenuItem.Command directly to the RoutedCommand can never find the CommandBindings registered
    // on TextArea and silently does nothing. Executing against TextArea explicitly on Click sidesteps
    // that broken focus-based resolution. See ANALYSIS.md #34 for the full diagnosis.
    private void SetupEditContextMenu()
    {
        var copy = CreateEditMenuItem("Copy", ApplicationCommands.Copy);
        var cut = CreateEditMenuItem("Cut", ApplicationCommands.Cut);
        var paste = CreateEditMenuItem("Paste", ApplicationCommands.Paste);

        var menu = new ContextMenu { ItemsSource = new[] { copy, cut, paste } };
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = ApplicationCommands.Copy.CanExecute(null, TextArea);
            cut.IsEnabled = ApplicationCommands.Cut.CanExecute(null, TextArea);
            paste.IsEnabled = ApplicationCommands.Paste.CanExecute(null, TextArea);
        };
        ContextMenu = menu;
    }

    private MenuItem CreateEditMenuItem(string header, RoutedCommand command)
    {
        var item = new MenuItem { Header = header, InputGesture = command.Gesture };
        item.Click += (_, _) => command.Execute(null, TextArea);
        return item;
    }

    private void ApplyOptions()
    {
        ShowBreakpoints = _editorOptions.ShowBreakpoints;
        ShowFolding = _editorOptions.ShowFolding;
        Options.ColumnRulerPositions = _editorOptions.ColumnRulerPositions;
    }

    private void OnShowBreakpointsChanged(bool? showBreakpoints)
    {
        var leftMargins = TextArea.LeftMargins;
        if (showBreakpoints == true)
            leftMargins.Insert(0, _breakpointMargin);
        else
            leftMargins.Remove(_breakpointMargin);
    }

    private void OnShowFoldingChanged(bool? show)
    {
        if (show == true)
        {
            if (_isLoaded && _foldingManager is null)
                InstallFolding();
            // else: deferred to Loaded event
        }
        else if (_foldingManager is not null)
        {
            UninstallFolding();
        }
    }

    private void InstallFolding()
    {
        _foldingManager = FoldingManager.Install(TextArea);

        // Replace AvaloniaEdit's default FoldingMargin with our VS Code-style one.
        // FoldingManager.Install adds a FoldingMargin; we swap it out immediately.
        var defaultMargin = TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (defaultMargin is not null)
        {
            var idx = TextArea.LeftMargins.IndexOf(defaultMargin);
            TextArea.LeftMargins.Remove(defaultMargin);
            _foldingMargin = new VsCodeFoldingMargin(_foldingManager);
            TextArea.LeftMargins.Insert(idx, _foldingMargin);
        }

        UpdateFoldings();
    }

    private void UninstallFolding()
    {
        if (_foldingMargin is not null)
        {
            TextArea.LeftMargins.Remove(_foldingMargin);
            _foldingMargin = null;
        }
        FoldingManager.Uninstall(_foldingManager!);
        _foldingManager = null;
    }

    private void UpdateFoldings()
    {
        if (_foldingManager is null || Document is null) return;
        _foldingStrategy.UpdateFoldings(_foldingManager, Document);
        _foldingMargin?.InvalidateVisual();
    }

    private void UpdateBraceMatch()
    {
        if (Document is null) { _braceMatchHighlighter.Clear(); return; }
        var (open, close, matched) = FindBraceMatch(TextArea.Caret.Offset);
        if (open < 0 && close < 0)
            _braceMatchHighlighter.Clear();
        else
            _braceMatchHighlighter.Update(open, close, matched);
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ch is a closing brace (a value in BracePairs) — returns its opening char. BracePairs only ever
    // has a handful of entries, so a linear scan for the reverse lookup is not worth a second dict.
    private bool TryGetOpenBrace(char closeChar, out char openChar)
    {
        foreach (var pair in BracePairs)
        {
            if (pair.Value != closeChar) continue;
            openChar = pair.Key;
            return true;
        }
        openChar = default;
        return false;
    }

    private (int open, int close, bool matched) FindBraceMatch(int caretOffset)
    {
        var doc = Document;
        if (doc is null) return (-1, -1, false);
        var text = doc.Text;
        int len = text.Length;

        // Determine which brace to match against: prefer char at caret, then char before caret.
        char ch;
        int checkOffset;
        bool isOpen, isClose;
        char closeChar = default, openChar = default;

        if (caretOffset < len)
        {
            ch = text[caretOffset];
            checkOffset = caretOffset;
            isOpen = BracePairs.TryGetValue(ch, out closeChar);
            isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
            if (!isOpen && !isClose && caretOffset > 0)
            {
                ch = text[caretOffset - 1];
                checkOffset = caretOffset - 1;
                isOpen = BracePairs.TryGetValue(ch, out closeChar);
                isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
            }
        }
        else if (caretOffset > 0)
        {
            ch = text[caretOffset - 1];
            checkOffset = caretOffset - 1;
            isOpen = BracePairs.TryGetValue(ch, out closeChar);
            isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
        }
        else return (-1, -1, false);

        if (isOpen) // opening brace — search forward
        {
            int depth = 1;
            for (int i = checkOffset + 1; i < len; i++)
            {
                if (text[i] == ch) depth++;
                else if (text[i] == closeChar && --depth == 0)
                    return (checkOffset, i, true);
            }
            return (checkOffset, -1, false); // unmatched open
        }
        else if (isClose) // closing brace — search backward
        {
            int depth = 1;
            for (int i = checkOffset - 1; i >= 0; i--)
            {
                if (text[i] == ch) depth++;
                else if (text[i] == openChar && --depth == 0)
                    return (i, checkOffset, true);
            }
            return (-1, checkOffset, false); // unmatched close
        }

        return (-1, -1, false);
    }

    private void JumpToMatchingBrace()
    {
        var caret = TextArea.Caret.Offset;
        var (open, close, matched) = FindBraceMatch(caret);
        if (!matched || open < 0 || close < 0) return;
        // Jump to the opposite end of the pair
        TextArea.Caret.Offset = (caret <= open + 1) ? close : open;
    }

    private bool TryExpandSnippet()
    {
        if (Snippets.Count == 0 || Document is null) return false;
        var offset = TextArea.Caret.Offset;
        if (offset == 0) return false;

        int wordEnd = offset;
        int wordStart = wordEnd;
        while (wordStart > 0 && char.IsLetterOrDigit(Document.GetCharAt(wordStart - 1)))
            wordStart--;

        if (wordStart == wordEnd) return false;
        var word = Document.GetText(wordStart, wordEnd - wordStart);
        if (!Snippets.TryGetValue(word, out var template)) return false;

        ExpandSnippet(wordStart, wordEnd - wordStart, template);
        return true;
    }

    // Expands a snippet template at [offset, offset+length) — used by both Tab-expansion above and
    // SnippetCompletionData (completion-list entry) — re-indenting it to match the current line and
    // placing the caret at the ${0} cursor marker (or the end of the expansion if there isn't one).
    private void ExpandSnippet(int offset, int length, string template)
    {
        var caretLine = Document.GetLineByOffset(offset);
        var lineText = Document.GetText(caretLine.Offset, caretLine.Length);
        int indentLen = 0;
        while (indentLen < lineText.Length && (lineText[indentLen] == ' ' || lineText[indentLen] == '\t'))
            indentLen++;
        var baseIndent = lineText[..indentLen];
        var tab = Options.IndentationString;

        var expansion = template.Replace("\n", "\n" + baseIndent).Replace("\t", tab);

        const string cursorMarker = "${0}";
        var cursorIdx = expansion.IndexOf(cursorMarker, StringComparison.Ordinal);
        int cursorOffset;
        if (cursorIdx >= 0)
        {
            expansion = expansion.Remove(cursorIdx, cursorMarker.Length);
            cursorOffset = offset + cursorIdx;
        }
        else
        {
            cursorOffset = offset + expansion.Length;
        }

        Document.Replace(offset, length, expansion);
        TextArea.Caret.Offset = cursorOffset;
    }

    // True right after a member-access trigger character (skipping back over the identifier prefix
    // already being typed) — the same spot completion is triggered by typing that character itself
    // (see CompletionTriggers). Snippets don't make sense there (nobody wants a `for` snippet offered
    // next to `.Length`/`.Count`), so ShowCompletion uses this to suppress them on an explicit
    // Ctrl+Space invoke in that context.
    private bool IsMemberAccessContext(int offset) =>
        IsMemberAccessContext(Document.Text, offset, IdentifierChars, CompletionTriggers);

    internal static bool IsMemberAccessContext(
        string text, int offset, IReadOnlySet<char> identifierChars, IReadOnlySet<char> triggerChars)
    {
        // offset can be stale relative to text — ShowCompletion captures the caret offset before an
        // await, and the document may have since changed (fast typing/backspacing during that gap) —
        // clamp instead of indexing past the current end.
        var pos = Math.Clamp(offset, 0, text.Length);
        while (pos > 0 && (char.IsLetterOrDigit(text[pos - 1]) || identifierChars.Contains(text[pos - 1])))
            pos--;
        return pos > 0 && triggerChars.Contains(text[pos - 1]);
    }

    private bool IsStartOfNewIdentifier() =>
        IsStartOfNewIdentifier(Document.Text, TextArea.Caret.Offset, IdentifierChars, CompletionTriggers);

    // True when the character just inserted at caretOffset starts a fresh identifier, not a
    // continuation of one already being typed (handled by AvaloniaEdit's own client-side filtering,
    // see OnTextEntered) and not the first letter after an already-registered trigger character
    // (that spot already re-invokes via CompletionTriggers, with context-specific results — piling
    // the general symbol list on top one keystroke later would just stomp on it). A free function of
    // (text, caretOffset, ...) — not just testable without a live Document/TextArea, but also so it
    // stays free of any particular language's lexical rules (letters/digits are the only universal
    // baseline; everything else is caller-supplied).
    internal static bool IsStartOfNewIdentifier(
        string text, int caretOffset, IReadOnlySet<char> identifierChars, IReadOnlySet<char> triggerChars)
    {
        var pos = Math.Clamp(caretOffset, 0, text.Length) - 1; // the character just typed
        if (pos <= 0) return true;
        var prev = text[pos - 1];
        if (char.IsLetterOrDigit(prev) || identifierChars.Contains(prev)) return false;
        return !triggerChars.Contains(prev);
    }

    private bool IsContextualTriggerContext() =>
        IsContextualTriggerContext(Document.Text, TextArea.Caret.Offset, IdentifierChars);

    // True when the character just inserted at caretOffset (one of ContextualTriggers) immediately
    // follows an identifier — e.g. the '<' in "List<". A free function of (text, caretOffset, ...)
    // for the same reasons as IsStartOfNewIdentifier above.
    internal static bool IsContextualTriggerContext(string text, int caretOffset, IReadOnlySet<char> identifierChars)
    {
        var pos = Math.Clamp(caretOffset, 0, text.Length) - 1; // the trigger character just typed
        if (pos <= 0) return false;
        var prev = text[pos - 1];
        return char.IsLetterOrDigit(prev) || identifierChars.Contains(prev);
    }

    private bool IsWordTriggerContext() =>
        IsWordTriggerContext(Document.Text, TextArea.Caret.Offset, IdentifierChars, WordTriggers);

    // True when the space just inserted at caretOffset immediately follows one of wordTriggers as a
    // whole word (not, say, "renew " matching "new") — e.g. "new " in C#/VB. A free function of
    // (text, caretOffset, ...) for the same reasons as IsStartOfNewIdentifier above.
    internal static bool IsWordTriggerContext(
        string text, int caretOffset, IReadOnlySet<char> identifierChars, IReadOnlySet<string> wordTriggers)
    {
        if (wordTriggers.Count == 0) return false;

        var end = Math.Clamp(caretOffset, 0, text.Length) - 1; // the space just typed
        if (end <= 0) return false;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || identifierChars.Contains(text[start - 1])))
            start--;
        if (start == end) return false; // no word immediately before the space

        return wordTriggers.Contains(text[start..end]);
    }

    private void OnFontSizeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        FontSize = e.Delta.Y > 0 ? FontSize + 1 : Math.Max(1, FontSize - 1);
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } && _completionWindow != null && !char.IsLetterOrDigit(e.Text[0]))
            _completionWindow.CompletionList.RequestInsertion(e);

        if (e.Text is not { Length: 1 } || e.Handled) return;
        var ch = e.Text[0];

        // Only close the insight window when exiting the argument list.
        if (BracePairs.ContainsValue(ch) || StatementTerminators.Contains(ch))
            _insightWindow?.Hide();

        // Skip-ahead: if the closing bracket is already at caret, move past it instead of inserting.
        if (BracePairs.ContainsValue(ch))
        {
            var offset = TextArea.Caret.Offset;
            if (offset < Document.TextLength && Document.GetCharAt(offset) == ch)
            {
                TextArea.Caret.Offset = offset + 1;
                e.Handled = true;
            }
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 }) return;
        var ch = e.Text[0];

        if (CompletionTriggers.Contains(ch))
            ShowCompletion(ch);
        else if (OverloadTriggers.Contains(ch))
            ShowOverloads(ch);
        // 24/7 auto-invoke (TODO.md #51) — fires once, on the first letter of a fresh identifier, not
        // on every keystroke: AvaloniaEdit's own CompletionWindow re-filters what's already fetched as
        // more letters are typed (including camelCase — see CompletionList.GetMatchQuality), the same
        // way it already does after a '.' trigger, so this adds no extra per-keystroke language-service
        // calls beyond today's dot-completion.
        else if (_completionWindow is null && (char.IsLetter(ch) || IdentifierChars.Contains(ch)) && IsStartOfNewIdentifier())
            ShowCompletion(ch, explicitInvoke: true);
        // Generics (TODO.md #51) — e.g. '<' right after "List" opens completion for the type argument.
        else if (_completionWindow is null && ContextualTriggers.Contains(ch) && IsContextualTriggerContext())
            ShowCompletion(ch, explicitInvoke: true);
        // "new " (TODO.md #51) — opens completion right where a constructible type goes.
        else if (_completionWindow is null && ch == ' ' && IsWordTriggerContext())
            ShowCompletion(ch, explicitInvoke: true);

        // Brace completion: insert the matching closing bracket.
        if (AutoCloseBraces.Contains(ch) && BracePairs.TryGetValue(ch, out var closing))
            Document.Insert(TextArea.Caret.Offset, closing.ToString());
    }

    private async void ShowCompletion(char trigger, bool explicitInvoke = false)
    {
        var args = new CompletionRequestedEventArgs(trigger, TextArea.Caret.Offset, explicitInvoke);

        if (AsyncCompletionProvider != null)
            await AsyncCompletionProvider(args);
        else
            CompletionRequested?.Invoke(this, args);

        // Snippets only make sense as statement-level suggestions, so only add them for an
        // explicit Ctrl+Space invoke — not e.g. after a '.' member access, where regular completion
        // is what the user is actually after.
        if (explicitInvoke && !IsMemberAccessContext(args.CaretOffset))
            foreach (var (name, template) in Snippets)
                args.Items.Add(new SnippetCompletionData(
                    name, template, SnippetIcon, segment => ExpandSnippet(segment.Offset, segment.Length, template)));

        if (args.Items.Count == 0) return;

        _completionWindow = new CompletionWindow(TextArea);
        _completionWindow.Closed += (_, _) => _completionWindow = null;

        // The window itself is a bare Popup with no Font/Background/Foreground of its own — the
        // actual visible content is CompletionList, a separate TemplatedControl that doesn't
        // inherit these from this editor through the visual tree, so copy them explicitly instead
        // of falling back to AvaloniaEdit's own default theme.
        _completionWindow.CompletionList.FontFamily = FontFamily;
        _completionWindow.CompletionList.FontSize = FontSize;
        _completionWindow.CompletionList.Background = Background;
        _completionWindow.CompletionList.Foreground = Foreground;

        foreach (var item in args.Items)
            _completionWindow.CompletionList.CompletionData.Add(item);

        _completionWindow.Show();
    }

    private async void ShowOverloads(char trigger)
    {
        var args = new OverloadRequestedEventArgs(trigger, TextArea.Caret.Offset);

        if (AsyncOverloadProvider != null)
            await AsyncOverloadProvider(args);
        else
            OverloadRequested?.Invoke(this, args);

        if (args.Overloads.Count == 0) return;

        _insightWindow?.Close();
        _insightWindow = new OverloadInsightWindow(TextArea);
        _insightWindow.Closed += (_, _) => _insightWindow = null;
        // Same reasoning as ShowCompletion above — style the popup's actual content control.
        if (_insightWindow.Child is TemplatedControl content)
        {
            content.FontFamily = FontFamily;
            content.FontSize = FontSize;
            content.Background = Background;
            content.Foreground = Foreground;
        }
        _insightWindow.Provider = new OverloadProvider(args.Overloads);
        _insightWindow.Show();
    }

    public int GetOffsetFromPoint(Point point)
    {
        var position = GetPositionFromPoint(point);
        return position != null ? Document.GetOffset(position.Value.Location) : -1;
    }

    public string SetLanguage(ThemeName theme, string languageExtension)
    {
        _registryOptions = new RegistryOptions(theme);
        _textMateInstallation?.Dispose();
        _textMateInstallation = this.InstallTextMate(_registryOptions);

        var language = _registryOptions.GetLanguageByExtension(languageExtension);
        var scopeName = _registryOptions.GetScopeByLanguageId(language.Id);
        _textMateInstallation.SetGrammar(scopeName);
        return scopeName;
    }

    public void SetTheme(ThemeName theme)
    {
        if (_textMateInstallation is null || _registryOptions is null) return;
        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(theme));
    }

    private sealed class OverloadProvider(IReadOnlyList<(object Header, object? Content)> items)
        : IOverloadProvider
    {
        private int _selectedIndex;

        public int Count => items.Count;

        public string CurrentIndexText => $"{_selectedIndex + 1} of {items.Count}";

        public object CurrentHeader => items[_selectedIndex].Header;

        public object? CurrentContent => items[_selectedIndex].Content;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
