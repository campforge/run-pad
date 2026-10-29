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

namespace Invoklet.Avalonia.CodeTextEditor;

public class CodeTextEditor : TextEditor, IDisposable
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
    private readonly GutterSeparatorMargin _gutterSeparator = new();
    private int _gutterCaretLine;
    private readonly OccurrenceHighlighter _occurrenceHighlighter = new();
    // Debounce before recomputing folding regions after a text change — long enough that fast
    // typing doesn't re-run the folding strategy on every keystroke.
    private static readonly TimeSpan FoldingDebounce = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _foldingTimer;
    private FoldingManager? _foldingManager;
    private FoldingMargin? _foldingMargin;
    private bool _isLoaded;
    private CodeTextEditorOptions _editorOptions = new();
    private RegistryOptions? _registryOptions;
    private Installation? _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private CancellationTokenSource? _completionCts;
    private CancellationTokenSource? _insightCts;
    private CancellationTokenSource? _occurrencesCts;
    // The caret offset right after the "(" that opened the currently-showing insight window — set
    // once, on the transition from closed to open, not touched by the caret-move/comma re-requests
    // that keep refreshing the SAME session (see OnTextEntering's nesting
    // check below, the reason this is tracked at all).
    private int? _insightAnchorOffset;


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

    /// <summary>Async provider for "highlight every occurrence of the symbol under the caret" —
    /// re-queried once the caret has rested for <see cref="OccurrenceHighlightDelay"/>.</summary>
    public Func<OccurrencesRequestedEventArgs, Task>? AsyncOccurrencesProvider { get; set; }

    /// <summary>
    /// How long the caret has to rest before occurrences are looked up. Every typed character moves the
    /// caret, and each lookup is a full semantic Roslyn query competing with the background analysis —
    /// without this, typing ran one per keystroke (editor smoothness, 2026-10-12).
    /// </summary>
    public TimeSpan OccurrenceHighlightDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Fired on F2 — the host resolves what's
    /// renameable at the current caret position and drives the actual rename UI/edit; this control
    /// only recognizes the keypress and hands off, the same shape as
    /// <see cref="BreakpointRightClicked"/>.</summary>
    public event EventHandler? RenameSymbolRequested;

    /// <summary>Fired on Shift+F12 ("Find All References") — same hand-off shape as
    /// <see cref="RenameSymbolRequested"/>: this control only recognizes the keypress, the host
    /// resolves and shows the actual reference list.</summary>
    public event EventHandler? FindReferencesRequested;

    /// <summary>Fired on plain F12 ("Go to Definition"/"Go to Implementation")
    /// — same hand-off shape as <see cref="RenameSymbolRequested"/>: this control
    /// only recognizes the keypress, the host decides where to go (prefers implementations when the
    /// symbol has any, falls back to the symbol's own definition otherwise). Deliberately a single
    /// binding, not a separate Ctrl+F12 for implementations — modifier+F-key combos collide with
    /// desktop-environment-level global shortcuts often enough on Linux (user feedback 2026-07-19,
    /// after trying Ctrl+F12 here) that it isn't a safe default to add another one.</summary>
    public event EventHandler? GoToDefinitionRequested;

    // The editor's own key commands. The defaults are the Windows/Linux ones; Invoklet sets them from
    // Preferences → Keyboard (and to the macOS defaults on a Mac). Null disables a command's key.
    public KeyGesture? CompletionGesture { get; set; } = new(Key.Space, KeyModifiers.Control);
    public KeyGesture? SignatureHelpGesture { get; set; } = new(Key.Space, KeyModifiers.Control | KeyModifiers.Shift);
    public KeyGesture? JumpToBracketGesture { get; set; } = new(Key.OemCloseBrackets, KeyModifiers.Control);
    public KeyGesture? RenameSymbolGesture { get; set; } = new(Key.F2);
    public KeyGesture? GoToDefinitionGesture { get; set; } = new(Key.F12);
    public KeyGesture? FindReferencesGesture { get; set; } = new(Key.F12, KeyModifiers.Shift);
    public KeyGesture? ToggleLineCommentGesture { get; set; } = new(Key.OemQuestion, KeyModifiers.Control);

    /// <summary>The language's line-comment prefix (<c>//</c>, <c>'</c>) used by
    /// <see cref="ToggleLineComment"/>; null or empty for a language without one, where the command
    /// does nothing.</summary>
    public string? LineCommentPrefix { get; set; }

    /// <summary>The name of a <see cref="Snippets"/> entry — a property template, say — offered on
    /// a blank line where a type's first member goes, right after Enter there. The offer is a hint at
    /// the caret: Tab inserts the snippet, anything else leaves the text as it is. Null for none.</summary>
    public string? MemberTemplateSnippet { get; set; }

    /// <summary>What the offer hint calls <see cref="MemberTemplateSnippet"/> ("Insert property").</summary>
    public string? MemberTemplateTitle { get; set; }

    /// <summary>Decides whether a position (in the given text) is where
    /// <see cref="MemberTemplateSnippet"/> belongs; the language service answers off the UI thread.
    /// Without it nothing is offered.</summary>
    public Func<string, int, CancellationToken, Task<bool>>? MemberTemplatePositionProvider { get; set; }

    /// <summary>Whether the member template hint is showing.</summary>
    public bool IsMemberTemplateOfferOpen => _memberOffer is not null;

    // The latest check for an offer, for tests to await.
    internal Task MemberTemplateOfferTask { get; private set; } = Task.CompletedTask;
    internal MemberTemplateOffer? MemberTemplateOfferForTests => _memberOffer;

    private MemberTemplateOffer? _memberOffer;
    private CancellationTokenSource? _memberOfferCts;

    /// <summary>The modifier held for mouse actions such as zooming with the wheel: Ctrl, or Cmd on
    /// macOS.</summary>
    public KeyModifiers CommandModifier { get; set; } = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static bool Matches(KeyGesture? gesture, KeyEventArgs e) => gesture?.Matches(e) == true;

    public IEnumerable<IBreakpoint> Breakpoints => _breakpointStore;

    public void AddBreakpoint(int line) => _breakpointStore.Add(line);
    public void RemoveBreakpoint(IBreakpoint bp) => _breakpointStore.Remove(bp);

    /// <summary>Redraws the breakpoint margin — call after updating a breakpoint's VerificationState.</summary>
    public void InvalidateBreakpoints() => _breakpointMargin.InvalidateVisual();

    /// <summary>Updates the lightbulb quick-fix gutter (e.g. after a diagnostics pass finds a missing using).</summary>
    public void UpdateCodeFixes(IReadOnlyList<CodeFixEntry> fixes) => _codeFixMargin.Update(fixes);

    /// <summary>Test-only observability — same rationale as every other "internal test hook" this
    /// session (e.g. DecompiledViewWindow.Editor): lets a test confirm what UpdateCodeFixes actually
    /// did without simulating a real paint pass.</summary>
    internal CodeFixSeverity? WorstCodeFixSeverityOnLine(int line) => _codeFixMargin.WorstSeverityOnLine(line);

    // Test-only (InternalsVisibleTo): the gutter's folding margin and separator, and the brace pair
    // highlighter.
    internal FoldingMargin? FoldingMarginForTests => _foldingMargin;
    internal GutterSeparatorMargin GutterSeparatorForTests => _gutterSeparator;
    internal BraceMatchHighlighter BraceMatchHighlighterForTests => _braceMatchHighlighter;

    /// <summary>Fired when the user clicks a specific lightbulb menu entry — see CodeFixEntry's own
    /// doc comment for why materializing/applying it is left entirely to whoever owns
    /// IIntelliSenseService.</summary>
    public event Action<CodeFixEntry>? FixApplyRequested;

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

    /// <summary>Whether a completion dropdown is currently showing.</summary>
    public bool IsCompletionWindowOpen => _completionWindow is not null;

    /// <summary>Whether the overload/parameter-info insight popup is currently showing.</summary>
    public bool IsInsightWindowOpen => _insightWindow is not null;

    /// <summary>Test-only observability for the "highlight every occurrence" feature.</summary>
    internal IReadOnlyList<(int Start, int Length)> HighlightedOccurrences => _occurrenceHighlighter.Spans;

    public void SetHoverTokenHighlight(int start, int length, IBrush brush)
    {
        _occurrenceHighlighter.HighlightBrush = brush;
        _occurrenceHighlighter.Update(Document, [(start, length)]);
        TextArea.TextView.Redraw();
    }

    public void ClearHoverTokenHighlight()
    {
        _occurrenceHighlighter.Clear();
        TextArea.TextView.Redraw();
    }

    /// <summary>Characters that open the completion dropdown.</summary>
    public HashSet<char> CompletionTriggers { get; } = [];

    /// <summary>
    /// Whether typing a commit character (see <see cref="IsCommitCharacter(char, IReadOnlySet{char})"/>)
    /// accepts the completion window's current selection, on top of always inserting the character
    /// itself. Defaults to <see langword="true"/> (today's only behavior); a user who finds
    /// punctuation unpredictably accepting a completion can turn this off (a global
    /// switch) — Enter/Tab/click still commit either way, this only gates the "typing punctuation
    /// also commits" convenience path.
    /// </summary>
    public bool CommitCharactersEnabled { get; set; } = true;

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
    /// keyword spelling, whether it's case-sensitive), so the app layer configures this. Settable
    /// (not just an empty collection to <c>Add</c> into, unlike the other trigger sets on this
    /// class) so a case-insensitive language (VB) can hand in a set built with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> and keep that comparer — copying its entries
    /// one at a time into a fresh, default-comparer <c>HashSet</c> would silently drop it.</summary>
    public IReadOnlySet<string> WordTriggers { get; set; } = new HashSet<string>();

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
    ///.</summary>
    public HashSet<char> AutoCloseBraces { get; } = [];

    /// <summary>Characters that end a statement, closing the signature-help insight window if it's
    /// still open (e.g. <c>;</c> in C#) — not every language ends statements the same way (VB doesn't
    /// require one at all), so the app layer configures this.</summary>
    public HashSet<char> StatementTerminators { get; } = [];

    /// <summary>Which code-folding algorithm to run — not every language delimits blocks with braces
    /// (e.g. VB's Sub/End Sub, If/End If), so the app layer configures this per language.
    /// Defaults to brace-based, matching this control's previous hardcoded behavior.</summary>
    public IFoldingStrategy FoldingStrategy { get; set; } = new BraceFoldingStrategy();

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
        _codeFixMargin.FixApplyRequested += fix => FixApplyRequested?.Invoke(fix);
        TextArea.LeftMargins.Add(_codeFixMargin);
        TextArea.LeftMargins.Add(_gutterSeparator);
        _debugLineHighlighter = new DebugLineHighlighter();

        _foldingTimer = new DispatcherTimer { Interval = FoldingDebounce };
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
        TextArea.TextView.BackgroundRenderers.Add(_occurrenceHighlighter);
        TextArea.Caret.PositionChanged += (_, _) => UpdateBraceMatch();
        TextArea.Caret.PositionChanged += (_, _) => UpdateGutterCaretLine();
        TextArea.Caret.PositionChanged += (_, _) => UpdateOccurrenceHighlights();
        // The active (bold) parameter used to update only when a "," was
        // typed — clicking, arrow keys, Home/End, etc. left it stuck on whatever slot was active when
        // the window opened. ShowOverloads is a stateless "recompute from the current caret position"
        // request (TryGetSignatureHelpAsync derives argIndex purely from position), so simply
        // re-triggering it on every caret move while the window is already open is enough — no
        // separate incremental-update path needed. '\0' — same "not a real typed character" sentinel
        // Ctrl+Space already uses for ShowCompletion's explicit invoke — is unused by the trigger
        // character on this path (TryGetSignatureHelpAsync doesn't branch on it, only on position).
        TextArea.Caret.PositionChanged += (_, _) => { if (IsInsightWindowOpen) ShowOverloads('\0'); };
        // The member template offer is for the spot it appeared at only.
        TextArea.Caret.PositionChanged += (_, _) => DismissMemberTemplateOffer();
        TextArea.LostFocus += (_, _) => DismissMemberTemplateOffer();

        Options.AllowToggleOverstrikeMode = true;
        Options.ShowBoxForControlCharacters = true;

        // Bug report 2026-07-27: AvaloniaEdit's built-in link detector (TextView's own
        // LinkElementGenerator/MailLinkElementGenerator, added automatically whenever these Options
        // default to true) runs a plain regex over each line's RAW TEXT — it has no idea about
        // TextMate/Roslyn syntax highlighting, so a URL sitting inside a C# string literal (extremely
        // common — connection strings, API endpoints, "https://test.com/") got wrapped in a
        // VisualLineLinkText element too, overriding the string's normal syntax color/style. There's no
        // way to make the built-in generator string/comment-aware short of reimplementing it against
        // real syntax boundaries — disabling it is the correct fix for a code editor (VS Code and
        // friends don't linkify arbitrary source text either).
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        TextArea.RightClickMovesCaret = true;

        TextArea.TextView.LineTransformers.Add(new UnderlineAndStrikeThroughTransformer());
        TextArea.TextView.ElementGenerators.Add(InlineControls);

        TextArea.TextEntering += OnTextEntering;
        TextArea.TextEntered += OnTextEntered;

        TextChanged += (_, _) => { _foldingTimer.Stop(); _foldingTimer.Start(); };
        TextChanged += (_, _) => DismissMemberTemplateOffer();

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

        // Intercept Tab (snippet expansion) and the editor's key commands (brace navigation, rename
        // symbol…) before AvaloniaEdit's built-in KeyBinding handlers fire. Attached to TextArea, not the
        // Window — this is deliberately how F2 here avoids colliding with FileTreeView's own F2
        // (rename FILE, KeyboardSettings.RenameFile): that handler is likewise scoped to the file
        // tree's own TreeView, so ordinary Avalonia routing (starting from whichever control
        // actually has keyboard focus) keeps the two from ever double-firing on the same keypress —
        // no explicit focus check needed on either side.
        TextArea.AddHandler(
            KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                // After the newline is in: is this where a type's first member goes?
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _completionWindow is null)
                    ScheduleMemberTemplateOffer();

                if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && TryExpandSnippet())
                    e.Handled = true;
                else if (Matches(JumpToBracketGesture, e))
                {
                    JumpToMatchingBrace();
                    e.Handled = true;
                }
                else if (Matches(ToggleLineCommentGesture, e) && !string.IsNullOrEmpty(LineCommentPrefix))
                {
                    ToggleLineComment();
                    e.Handled = true;
                }
                else if (Matches(SignatureHelpGesture, e))
                {
                    ShowOverloads('\0');
                    e.Handled = true;
                }
                else if (Matches(CompletionGesture, e))
                {
                    ShowCompletion('\0', explicitInvoke: true);
                    e.Handled = true;
                }
                else if (Matches(RenameSymbolGesture, e))
                {
                    RenameSymbolRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                else if (Matches(FindReferencesGesture, e))
                {
                    FindReferencesRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }
                else if (Matches(GoToDefinitionGesture, e))
                {
                    GoToDefinitionRequested?.Invoke(this, EventArgs.Empty);
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
    // that broken focus-based resolution.
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
        KeepGutterSeparatorLast();
    }

    // The separator sits between the whole gutter and the code, whichever margins come and go.
    private void KeepGutterSeparatorLast()
    {
        var leftMargins = TextArea.LeftMargins;
        if (leftMargins.Count > 0 && leftMargins[^1] == _gutterSeparator) return;
        leftMargins.Remove(_gutterSeparator);
        leftMargins.Add(_gutterSeparator);
    }

    // The gutter columns paint the caret's row: repaint them when the caret moves to another line.
    private void UpdateGutterCaretLine()
    {
        var line = TextArea.Caret.Line;
        if (line == _gutterCaretLine) return;
        _gutterCaretLine = line;
        _breakpointMargin.InvalidateVisual();
        _foldingMargin?.InvalidateVisual();
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
        var defaultMargin = TextArea.LeftMargins.OfType<AvaloniaEdit.Folding.FoldingMargin>().FirstOrDefault();
        if (defaultMargin is not null)
        {
            var idx = TextArea.LeftMargins.IndexOf(defaultMargin);
            TextArea.LeftMargins.Remove(defaultMargin);
            _foldingMargin = new FoldingMargin(_foldingManager);
            TextArea.LeftMargins.Insert(idx, _foldingMargin);
        }
        KeepGutterSeparatorLast();

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
        FoldingStrategy.UpdateFoldings(_foldingManager, Document);
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
        if (matched) _foldingMargin?.SetActiveBraces(open, close);
        else _foldingMargin?.SetActiveBraces(-1, -1);
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // Same cancel-a-superseded-request shape as ShowCompletion/
    // ShowOverloads: fast caret movement can fire several of these before the
    // first one's provider call resolves, so the previous one is cancelled rather than raced, and
    // its CTS deliberately never disposed (only ever Cancel()'d — a CTS with no timer holds no
    // resources, same reasoning as those two).
    private async void UpdateOccurrenceHighlights()
    {
        _occurrencesCts?.Cancel();
        if (Document is null || AsyncOccurrencesProvider is null)
        {
            _occurrenceHighlighter.Clear();
            TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }

        var cts = new CancellationTokenSource();
        _occurrencesCts = cts;

        try
        {
            // a newer caret move (the next typed character) cancels this before the provider runs
            if (OccurrenceHighlightDelay > TimeSpan.Zero) await Task.Delay(OccurrenceHighlightDelay, cts.Token);
            if (Document is null || AsyncOccurrencesProvider is not { } provider) return;
            var args = new OccurrencesRequestedEventArgs(TextArea.Caret.Offset, cts.Token);
            await provider(args);
            if (cts.IsCancellationRequested) return;
            _occurrenceHighlighter.Update(Document, args.Spans);
            TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)

        {

            _occurrenceHighlighter.Clear();

            TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

            return;

        }
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

    internal (int open, int close, bool matched) FindBraceMatch(int caretOffset)
    {
        var doc = Document;
        if (doc is null) return (-1, -1, false);
        int len = doc.TextLength;

        // Determine which brace to match against: prefer char at caret, then char before caret.
        char ch;
        int checkOffset;
        bool isOpen, isClose;
        char closeChar = default, openChar = default;

        if (caretOffset < len)
        {
            ch = doc.GetCharAt(caretOffset);
            checkOffset = caretOffset;
            isOpen = BracePairs.TryGetValue(ch, out closeChar);
            isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
            if (!isOpen && !isClose && caretOffset > 0)
            {
                ch = doc.GetCharAt(caretOffset - 1);
                checkOffset = caretOffset - 1;
                isOpen = BracePairs.TryGetValue(ch, out closeChar);
                isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
            }
        }
        else if (caretOffset > 0)
        {
            ch = doc.GetCharAt(caretOffset - 1);
            checkOffset = caretOffset - 1;
            isOpen = BracePairs.TryGetValue(ch, out closeChar);
            isClose = !isOpen && TryGetOpenBrace(ch, out openChar);
        }
        else return (-1, -1, false);

        // Read outward from the brace in chunks instead of copying the whole document into a string
        // on every caret move (every keystroke) — the match is nearly always within the first chunk.
        const int chunkSize = 4096;
        if (isOpen) // opening brace — search forward
        {
            int depth = 1;
            for (int chunkStart = checkOffset + 1; chunkStart < len; chunkStart += chunkSize)
            {
                var chunk = doc.GetText(chunkStart, Math.Min(chunkSize, len - chunkStart));
                for (int j = 0; j < chunk.Length; j++)
                {
                    if (chunk[j] == ch) depth++;
                    else if (chunk[j] == closeChar && --depth == 0)
                        return (checkOffset, chunkStart + j, true);
                }
            }
            return (checkOffset, -1, false); // unmatched open
        }
        else if (isClose) // closing brace — search backward
        {
            int depth = 1;
            for (int chunkEnd = checkOffset; chunkEnd > 0; chunkEnd -= chunkSize)
            {
                var chunkStart = Math.Max(0, chunkEnd - chunkSize);
                var chunk = doc.GetText(chunkStart, chunkEnd - chunkStart);
                for (int j = chunk.Length - 1; j >= 0; j--)
                {
                    if (chunk[j] == ch) depth++;
                    else if (chunk[j] == openChar && --depth == 0)
                        return (chunkStart + j, checkOffset, true);
                }
            }
            return (-1, checkOffset, false); // unmatched close
        }

        return (-1, -1, false);
    }

    /// <summary>Comments the lines the selection (or, without one, the caret) touches, or uncomments
    /// them when they are all comments already — see <see cref="LineCommentToggle"/>. One undo step.
    /// Returns whether the text changed.</summary>
    public bool ToggleLineComment()
    {
        if (IsReadOnly || Document is null || string.IsNullOrEmpty(LineCommentPrefix)) return false;
        var selection = TextArea.Selection;
        var (start, end) = selection.IsEmpty
            ? (TextArea.Caret.Offset, TextArea.Caret.Offset)
            : (selection.SurroundingSegment.Offset, selection.SurroundingSegment.EndOffset);
        var edits = LineCommentToggle.Compute(Document.Text, start, end, LineCommentPrefix);
        if (edits.Count == 0) return false;

        Document.BeginUpdate();
        try
        {
            for (var i = edits.Count - 1; i >= 0; i--)
                Document.Replace(edits[i].Offset, edits[i].RemoveLength, edits[i].Insert);
        }
        finally
        {
            Document.EndUpdate();
        }
        return true;
    }

    private void ScheduleMemberTemplateOffer()
    {
        if (MemberTemplatePositionProvider is null || string.IsNullOrEmpty(MemberTemplateSnippet)) return;
        Dispatcher.UIThread.Post(() => MemberTemplateOfferTask = OfferMemberTemplateAsync(), DispatcherPriority.Background);
    }

    private async Task OfferMemberTemplateAsync()
    {
        if (MemberTemplatePositionProvider is not { } provider || MemberTemplateSnippet is not { } name
            || !Snippets.ContainsKey(name) || IsReadOnly || Document is not { } document
            || _completionWindow is not null || _memberOffer is not null || !TextArea.IsKeyboardFocusWithin)
            return;
        var caret = TextArea.Caret.Offset;
        if (!string.IsNullOrWhiteSpace(document.GetText(document.GetLineByOffset(caret)))) return;

        _memberOfferCts?.Cancel();
        var cts = new CancellationTokenSource();
        _memberOfferCts = cts;
        var version = document.Version;
        bool fits;
        try
        {
            fits = await provider(document.Text, caret, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Only for the very spot that was asked about: no edit, caret move or other popup since.
        if (!fits || cts.IsCancellationRequested || !ReferenceEquals(Document, document)
            || version is null || document.Version is not { } now || version.CompareAge(now) != 0
            || TextArea.Caret.Offset != caret || _completionWindow is not null || _memberOffer is not null)
            return;
        var offer = new MemberTemplateOffer(this, name, MemberTemplateTitle ?? name);
        _memberOffer = offer;
        TextArea.PushStackedInputHandler(offer);
        offer.Show();
    }

    internal void AcceptMemberTemplateOffer()
    {
        if (_memberOffer is not { } offer) return;
        DismissMemberTemplateOffer();
        if (Snippets.TryGetValue(offer.Snippet, out var template))
            ExpandSnippet(TextArea.Caret.Offset, 0, template);
    }

    internal void DismissMemberTemplateOffer()
    {
        _memberOfferCts?.Cancel();
        if (_memberOffer is not { } offer) return;
        _memberOffer = null;
        TextArea.PopStackedInputHandler(offer);
        offer.Hide();
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
    // Public (not just used internally) so a language-aware caller building its own SnippetCompletionData
    // — e.g. EditorTabItemControl's override-member stubs — reuses this same
    // reindent/cursor-placement logic instead of duplicating it; nothing about the method itself is
    // C#-specific (the controls stay language-agnostic).
    public void ExpandSnippet(int offset, int length, string template)
    {
        // A template with fields becomes an interactive snippet: Tab moves between its fields.
        if (SnippetTemplate.TryCreate(template) is { } snippet)
        {
            Document.UndoStack.StartUndoGroup();
            try
            {
                Document.Remove(offset, length);
                TextArea.ClearSelection();
                TextArea.Caret.Offset = offset;
                snippet.Insert(TextArea);
            }
            finally
            {
                Document.UndoStack.EndUndoGroup();
            }
            return;
        }

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

    private bool IsCommitCharacter(char ch) => IsCommitCharacter(ch, IdentifierChars);

    // True for a just-typed character that should commit the completion window's selected item
    // (in addition to being inserted itself) rather than continuing to filter the open list.
    // Anything that can be part of an identifier — letters/digits, plus identifierChars like '_' —
    // must never commit: mid-identifier characters are just more of the word being typed, not a
    // request to accept a match ('_' was falling through the old `!IsLetterOrDigit`
    // check). Space is excluded unconditionally too: unlike Visual Studio's default, this matches
    // Rider — committing on space was swapping still-being-typed words for an unrelated prefix match
    // (e.g. "var ret " -> "var return ", the same class of bug as the earlier var/args report). '='
    // is excluded for the same reason — a lambda parameter name (e.g. "list.First(x" then "=" to
    // start "x => ...") auto-invokes on 'x' like any fresh identifier, and typing '=' right after
    // used to commit whatever that happened to match instead of just being inserted as the start of
    // "=>" (user-reported). A free function of (ch, identifierChars) for the same testability/
    // language-agnosticism reasons as IsStartOfNewIdentifier above.
    internal static bool IsCommitCharacter(char ch, IReadOnlySet<char> identifierChars) =>
        !char.IsLetterOrDigit(ch) && !identifierChars.Contains(ch) && ch != ' ' && ch != '=';

    /// <summary>
    /// Raised by Ctrl/Cmd+wheel with +1 (zoom in) or -1 (zoom out). An application that keeps one
    /// zoom for all its editors handles it and sets <see cref="TemplatedControl.FontSize"/> itself;
    /// with no handler the wheel changes this editor's FontSize by one point.
    /// </summary>
    public event Action<int>? ZoomRequested;

    private void OnFontSizeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != CommandModifier || e.Delta.Y == 0) return;
        var direction = e.Delta.Y > 0 ? 1 : -1;
        if (ZoomRequested is { } zoom)
            zoom(direction);
        else
            FontSize = Math.Max(1, FontSize + direction);
        e.Handled = true;
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (CommitCharactersEnabled && e.Text is { Length: > 0 } && _completionWindow != null && IsCommitCharacter(e.Text[0]))
            _completionWindow.CompletionList.RequestInsertion(e);

        if (e.Text is not { Length: 1 } || e.Handled) return;
        var ch = e.Text[0];

        // Only close the insight window when exiting the argument list — not when closing a bracket
        // NESTED inside one of its already-typed arguments ("Foo(new[] { 1, 2 }"
        // used to hide the window for Foo's still-open call on the array literal's own "}"). A
        // statement terminator has no such nesting concept — it always ends the whole statement.
        if (StatementTerminators.Contains(ch))
        {
            _insightWindow?.Hide();
        }
        else if (BracePairs.ContainsValue(ch) && !IsClosingANestedBracket())
        {
            _insightWindow?.Hide();
        }

        // Skip-ahead: if the closing bracket is already at caret, move past it instead of inserting.
        if (!BracePairs.ContainsValue(ch)) return;
        var offset = TextArea.Caret.Offset;
        if (offset >= Document.TextLength || Document.GetCharAt(offset) != ch) return;
        TextArea.Caret.Offset = offset + 1;
        e.Handled = true;
    }

    // True when the about-to-be-typed closing bracket closes something opened AFTER the insight
    // window's call started — i.e. a nested bracket inside an argument, not
    // the call's own closing bracket. A plain running depth count from the anchor to the caret, not a
    // real matched-pair scan: this only has to decide "hide or don't", and over/under-counting on
    // genuinely malformed in-progress code is an acceptable approximation for that, same spirit as the
    // rest of this file's position-based checks.
    private bool IsClosingANestedBracket()
    {
        if (_insightAnchorOffset is not { } anchor) return false;
        var caret = Math.Min(TextArea.Caret.Offset, Document.TextLength);
        if (caret <= anchor) return false;

        var depth = 0;
        for (var i = anchor; i < caret; i++)
        {
            var c = Document.GetCharAt(i);
            if (BracePairs.ContainsKey(c)) depth++;
            else if (BracePairs.ContainsValue(c) && depth > 0) depth--;
        }

        return depth > 0;
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 }) return;
        var ch = e.Text[0];

        if (CompletionTriggers.Contains(ch))
            ShowCompletion(ch);
        else if (OverloadTriggers.Contains(ch))
            ShowOverloads(ch);
        // 24/7 auto-invoke — fires once, on the first letter of a fresh identifier, not
        // on every keystroke: AvaloniaEdit's own CompletionWindow re-filters what's already fetched as
        // more letters are typed (including camelCase — see CompletionList.GetMatchQuality), the same
        // way it already does after a '.' trigger, so this adds no extra per-keystroke language-service
        // calls beyond today's dot-completion.
        else if (_completionWindow is null && (char.IsLetter(ch) || IdentifierChars.Contains(ch)) && IsStartOfNewIdentifier())
            ShowCompletion(ch, explicitInvoke: true);
        // Generics — e.g. '<' right after "List" opens completion for the type argument.
        else if (_completionWindow is null && ContextualTriggers.Contains(ch) && IsContextualTriggerContext())
            ShowCompletion(ch, explicitInvoke: true);
        // "new " — opens completion right where a constructible type goes.
        else if (_completionWindow is null && ch == ' ' && IsWordTriggerContext())
            ShowCompletion(ch, explicitInvoke: true);

        // Brace completion: insert the matching closing bracket.
        if (AutoCloseBraces.Contains(ch) && BracePairs.TryGetValue(ch, out var closing))
            Document.Insert(TextArea.Caret.Offset, closing.ToString());
    }

    private async void ShowCompletion(char trigger, bool explicitInvoke = false)
    {
        // Fast typing can start a new completion request before the previous one has resolved (e.g.
        // 'C' triggers the 24/7 auto-invoke, then '.' triggers again before that first request comes
        // back) — cancelling the previous request's token here means a provider that threads it
        // through to its own async work (Roslyn's SemanticModel calls) actually stops that work
        // instead of it running to completion and racing this one.
        // Deliberately NOT disposed here: the superseded request's provider call
        // may still be mid-flight past this point (e.g. suspended on an await inside
        // AsyncCompletionProvider), and any later attempt to register a callback against — or read
        // certain members of — an already-disposed CancellationTokenSource throws
        // ObjectDisposedException. A CancellationTokenSource that never has its WaitHandle touched
        // holds no unmanaged resources, so leaving it for the GC is safe.
        _completionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _completionCts = cts;

        var args = new CompletionRequestedEventArgs(trigger, TextArea.Caret.Offset, explicitInvoke, cts.Token);

        try
        {
            if (AsyncCompletionProvider != null)
                await AsyncCompletionProvider(args);
            else
                CompletionRequested?.Invoke(this, args);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request — nothing to show for a request nobody's waiting on.
            return;
        }

        // Belt-and-suspenders: a provider that doesn't observe the token (a test double, or the
        // synchronous CompletionRequested event) can still return normally after being superseded.
        if (cts.IsCancellationRequested) return;

        // The provider found the caret in a fresh, not-yet-typed call argument — the
        // user's Ctrl+Space is really asking for parameter info there, not a completion list.
        if (args.RedirectToOverloads)
        {
            ShowOverloads(trigger);
            return;
        }

        // Snippets only make sense as statement-level suggestions, so only add them for an
        // explicit Ctrl+Space invoke — not e.g. after a '.' member access, where regular completion
        // is what the user is actually after — and not when the provider says this position isn't a
        // valid completion spot at all (e.g. inside a comment/string; see SuppressSnippets).
        if (explicitInvoke && !IsMemberAccessContext(args.CaretOffset) && !args.SuppressSnippets)
            foreach (var (name, template) in Snippets)
                args.Items.Add(new SnippetCompletionData(
                    name, template, SnippetIcon, segment => ExpandSnippet(segment.Offset, segment.Length, template)));

        if (args.Items.Count == 0) return;

        var window = new CompletionWindow(TextArea);
        _completionWindow = window;
        // A newer ShowCompletion call can open (and even close) its own window before this one's
        // Closed event fires — only clear the field if it's still pointing at *this* window, so an
        // old window's closure can't null out a newer window's reference out from under it
        //.
        window.Closed += (_, _) => { if (ReferenceEquals(_completionWindow, window)) _completionWindow = null; };

        // The window itself is a bare Popup with no Font/Background/Foreground of its own — the
        // actual visible content is CompletionList, a separate TemplatedControl that doesn't
        // inherit these from this editor through the visual tree, so copy them explicitly instead
        // of falling back to AvaloniaEdit's own default theme.
        window.CompletionList.FontFamily = FontFamily;
        window.CompletionList.FontSize = FontSize;
        window.CompletionList.Background = Background;
        window.CompletionList.Foreground = Foreground;

        foreach (var item in args.Items)
            window.CompletionList.CompletionData.Add(item);

        // CompletionWindowBase defaults StartOffset to the caret position at construction time — for
        // a '.' trigger that's correct (nothing of the member name is typed yet), but the 24/7
        // auto-invoke (and Ctrl+Space pressed mid-word) opens the window *after* part of the word is
        // already in the document. Left alone, accepting an item would insert the full completion
        // text right after that already-typed prefix instead of replacing it (e.g. "Wri" + "WriteLine"
        // -> "WriWriteLine"). Walking back over the already-typed identifier fixes the replace range.
        window.StartOffset = GetWordStart(TextArea.Caret.Offset);

        // Tab should replace any pre-existing remainder of the word the caret is
        // inside (e.g. "Cons|ole" -> accepting "Console" via Tab should produce "Console", not
        // "Consoleole"), while Enter leaves it alone — see CompletionWindow.WordEndOffsetProvider.
        window.WordEndOffsetProvider = GetWordEnd;

        window.Show();

        // AvaloniaEdit only (re-)filters and pre-selects the best match from CaretPositionChanged
        // (see CompletionWindow's source) — which fires on the *next* keystroke, not for text already
        // typed before the window opened. The 24/7 auto-invoke opens right after the first letter of
        // the identifier is already in the document (see StartOffset above), so without this the
        // window would show the full, unfiltered, nothing-selected list for that first letter
        // — camelCase/fuzzy matching and pre-selection only kicking in from the
        // second letter onward.
        var alreadyTyped = Document.GetText(window.StartOffset, TextArea.Caret.Offset - window.StartOffset);
        if (alreadyTyped.Length > 0)
        {
            window.CompletionList.SelectItem(alreadyTyped);

            // CompletionWindow's own CaretPositionChanged handler is what normally hides the window
            // once filtering leaves nothing to show (e.g. none of the offered snippets/symbols start
            // with what's already typed) — but that handler only runs on the *next* caret move, not
            // for this direct call made right after Show() (see the comment above). Without
            // replicating its "nothing matched" check here too, the popup stayed open showing an
            // empty list instead of closing.
            if (window.CompletionList.ListBox.ItemCount == 0)
                window.Hide();
        }
    }

    private int GetWordStart(int offset) => GetWordStart(Document.Text, offset, IdentifierChars);

    // Walks back from offset over already-typed identifier characters to find where the current word
    // began, so a completion window opened mid-word (or one character late, as with the 24/7
    // auto-invoke) replaces that whole word instead of just appending after it. A free function of
    // (text, offset, ...) for the same reasons as IsStartOfNewIdentifier above.
    internal static int GetWordStart(string text, int offset, IReadOnlySet<char> identifierChars)
    {
        var pos = Math.Clamp(offset, 0, text.Length);
        while (pos > 0 && (char.IsLetterOrDigit(text[pos - 1]) || identifierChars.Contains(text[pos - 1])))
            pos--;
        return pos;
    }

    private int GetWordEnd(int offset) => GetWordEnd(Document.Text, offset, IdentifierChars);

    // Mirrors GetWordStart, walking forward instead — finds where a pre-existing remainder of the
    // word at offset ends, so a Tab-triggered commit (CompletionWindow.WordEndOffsetProvider) can
    // consume it instead of leaving it dangling after the inserted completion text.
    internal static int GetWordEnd(string text, int offset, IReadOnlySet<char> identifierChars)
    {
        var pos = Math.Clamp(offset, 0, text.Length);
        while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || identifierChars.Contains(text[pos])))
            pos++;
        return pos;
    }

    private async void ShowOverloads(char trigger)
    {
        // Same reasoning as ShowCompletion above — cancel a superseded request instead of racing it.
        // Not disposed here either — see ShowCompletion's comment.
        _insightCts?.Cancel();
        var cts = new CancellationTokenSource();
        _insightCts = cts;

        // Captured synchronously, before the await below — not after it. Two
        // ShowOverloads calls can be in flight back to back (e.g. a caret-move re-request superseded
        // by another one right after — see ShowCompletion's own note on this), and checking
        // "_insightWindow is null" only after each one's own await left a window for the later call to
        // still see a stale non-null _insightWindow from the FIRST call's not-yet-applied result,
        // wrongly treating what should be a fresh session as a mere refresh (or vice versa) and
        // corrupting the nesting-depth anchor below.
        var isNewSession = _insightWindow is null;
        var caretOffset = TextArea.Caret.Offset;
        var args = new OverloadRequestedEventArgs(trigger, caretOffset, cts.Token);

        try
        {
            if (AsyncOverloadProvider != null)
                await AsyncOverloadProvider(args);
            else
                OverloadRequested?.Invoke(this, args);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;

        // The caret can now be re-requesting from a position outside any call at all (this
        // runs on every caret move while the window is open, not just on a typed
        // ","), in which case the provider correctly comes back empty — close the now-stale window
        // instead of leaving it open and showing parameters for wherever it last was.
        if (args.Overloads.Count == 0)
        {
            _insightWindow?.Close();
            _insightAnchorOffset = null;
            return;
        }

        if (isNewSession) _insightAnchorOffset = args.CaretOffset;
        // Constructing a new OverloadInsightWindow while the previous one is still showing closes
        // that previous one as a side effect (an AvaloniaEdit/Popup implementation detail, not
        // something this code triggers directly) — and its Closed event fires synchronously, DURING
        // this very call, before _insightWindow below gets reassigned. A Closed handler that nulled
        // _insightAnchorOffset (the way it nulls _insightWindow) would catch that self-close and wipe
        // out the anchor this same call just set two lines above, for a window transition nobody
        // asked for. Left for _insightWindow only, whose staleness is otherwise harmless (a null
        // check gate everywhere it's read) — the anchor has no such gate, so it can't absorb this.
        _insightWindow?.Close();
        var window = new OverloadInsightWindow(TextArea);
        _insightWindow = window;
        // Same ReferenceEquals reasoning as ShowCompletion's window.Closed above.
        window.Closed += (_, _) => { if (ReferenceEquals(_insightWindow, window)) _insightWindow = null; };
        // Same reasoning as ShowCompletion above — style the popup's actual content control.
        if (window.Child is TemplatedControl content)
        {
            content.FontFamily = FontFamily;
            content.FontSize = FontSize;
            content.Background = Background;
            content.Foreground = Foreground;
        }
        window.Provider = new OverloadProvider(args.Overloads);
        window.Show();
    }

    public int GetOffsetFromPoint(Point point)
    {
        var position = GetPositionFromPoint(point);
        return position != null ? Document.GetOffset(position.Value.Location) : -1;
    }

    /// <summary>
    /// <see cref="SetLanguage"/> for an arbitrary file extension: false (plain text, nothing installed)
    /// when TextMate has no grammar for it — <see cref="SetLanguage"/> itself throws then.
    /// </summary>
    public bool TrySetLanguage(ThemeName theme, string fileExtension)
    {
        if (string.IsNullOrEmpty(fileExtension)
            || new RegistryOptions(theme).GetLanguageByExtension(fileExtension) is null)
            return false;
        SetLanguage(theme, fileExtension);
        return true;
    }

    // TextMate keeps a worker per installation until it is disposed, so an editor that is closed
    // without this leaves one thread-pool thread behind for good.
    public void Dispose()
    {
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
    }

    /// <summary>Stops colouring work while the editor is not in use; what is coloured stays. See
    /// <see cref="ResumeHighlighting"/>.</summary>
    public void FreezeHighlighting() => _textMateInstallation?.Freeze();

    public void ResumeHighlighting() => _textMateInstallation?.Resume();

    public string SetLanguage(ThemeName theme, string languageExtension)
    {
        _registryOptions = new RegistryOptions(theme);
        _textMateInstallation?.Dispose();
        _textMateInstallation = this.InstallTextMate(new IlAwareRegistryOptions(_registryOptions));
        GrammarScope = null; // the new installation has no grammar yet
        return SetGrammarByExtension(languageExtension);
    }

    /// <summary>
    /// Switches the installed highlighting to another language of the bundled grammars without
    /// reinstalling TextMate (the theme stays); a no-op before <see cref="SetLanguage"/>.
    /// </summary>
    public string SetGrammarByExtension(string languageExtension)
    {
        if (_textMateInstallation is null || _registryOptions is null) return "";
        var language = _registryOptions.GetLanguageByExtension(languageExtension);
        var scopeName = _registryOptions.GetScopeByLanguageId(language.Id);
        if (scopeName == GrammarScope) return scopeName;
        _textMateInstallation.SetGrammar(scopeName);
        GrammarScope = scopeName;
        return scopeName;
    }

    /// <summary>Highlights the text as IL (<see cref="IlGrammar"/>); a no-op before <see cref="SetLanguage"/>.</summary>
    public void SetIlGrammar()
    {
        if (_textMateInstallation is null || GrammarScope == IlGrammar.ScopeName) return;
        _textMateInstallation.SetGrammar(IlGrammar.ScopeName);
        GrammarScope = IlGrammar.ScopeName;
    }

    /// <summary>The TextMate scope of the active grammar; null until a language is installed.</summary>
    public string? GrammarScope { get; private set; }

    internal Installation? TextMateInstallation => _textMateInstallation;

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
