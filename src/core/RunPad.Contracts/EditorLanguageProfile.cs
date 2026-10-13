namespace RunPad.Contracts;

// Which built-in AvaloniaEdit smart-indentation behavior a language wants — kept as an enum rather
// than a concrete AvaloniaEdit type so this contract (like the rest of RunPad.Contracts) doesn't
// depend on any UI framework; EditorTabItemControl maps it to the real
// AvaloniaEdit.Indentation.IIndentationStrategy.
public enum IndentationStrategyKind
{
    // Just carries the previous line's leading whitespace forward — correct default for a language
    // with no brace-delimited blocks (e.g. VB's Sub/End Sub, If/End If).
    Default,

    // Indents after an opening brace, dedents before a closing one — C-family block syntax.
    CStyle,
}

// The editor-level lexical/syntactic conventions a language plugin wants applied to
// RunPad.Avalonia.CodeTextEditor, which is itself deliberately language-agnostic (see its own XML
// docs on IdentifierChars/ContextualTriggers/WordTriggers/BracePairs/AutoCloseBraces/
// StatementTerminators — all empty-by-default, meant to be configured externally rather than
// assuming any language's rules). Mirrors ILanguageHostFactory.Snippets, which already flows the
// same way from the language plugin to the editor — this closes the same gap for
// everything CodeTextEditor needs configured: before this, EditorTabItemControl
// hardcoded a single C#-shaped profile for every language, including VB.
public sealed class EditorLanguageProfile
{
    public IReadOnlySet<char> CompletionTriggers { get; init; } = new HashSet<char>();
    public IReadOnlySet<char> OverloadTriggers { get; init; } = new HashSet<char>();

    // Beyond letters/digits, which always count as identifier-continuation characters regardless
    // of language (see CodeTextEditor.IdentifierChars).
    public IReadOnlySet<char> IdentifierChars { get; init; } = new HashSet<char>();

    public IReadOnlySet<char> ContextualTriggers { get; init; } = new HashSet<char>();
    public IReadOnlySet<string> WordTriggers { get; init; } = new HashSet<string>();
    public IReadOnlySet<char> StatementTerminators { get; init; } = new HashSet<char>();
    public IReadOnlyDictionary<char, char> BracePairs { get; init; } = new Dictionary<char, char>();
    public IReadOnlySet<char> AutoCloseBraces { get; init; } = new HashSet<char>();
    public IndentationStrategyKind IndentationStrategy { get; init; } = IndentationStrategyKind.Default;

    // Used to be a FoldingStrategyKind enum (BraceBased/VbKeywordBased) that
    // EditorTabItemControl (main UI project) switched on to hardcode `new VbFoldingStrategy()` — a
    // main-project file directly naming a concrete VB-specific type. Replaced with the language
    // plugin handing over the actual algorithm as a plain delegate instead: text in, fold-region
    // offset pairs out. Plain string/int/tuple types only (no AvaloniaEdit types), so this stays as
    // UI-framework-independent as the rest of RunPad.Contracts while still letting a language project
    // own its own folding algorithm (see RunPad.Languages.VB.VbFoldingRegions.Compute) without
    // RunPad.Contracts or RunPad.Languages.* needing a reference to RunPad.Avalonia.CodeTextEditor —
    // which would otherwise force RunPad.Cli (references RunPad.Languages.CSharp/.VB directly, no
    // Avalonia today) to pull in the whole Avalonia UI stack just to run a .vb script. Null means
    // "no language-specific algorithm" — EditorTabItemControl falls back to brace-based folding,
    // correct for C#'s { }-delimited blocks.
    public Func<string, IReadOnlyList<(int Start, int End)>>? FoldingRegionsProvider { get; init; }

    // Used to be a `switch` on RpadLanguage hardcoded in FileTreeViewNode.LanguageIconKey
    // (main UI project) — same class of problem as FoldingRegionsProvider above, same fix: the
    // language plugin names its own icon instead of the UI layer knowing about every language by name.
    // A resource key into src/main/RunPad/Resources/Icons.axaml (StreamGeometry, resolved at bind time
    // via ResourceKeyToGeometryConverter); null means "no icon for this language yet" — the file tree
    // falls back to its generic file icon (FileTreeViewNode.ShowGenericFileIcon).
    public string? IconResourceKey { get; init; }
}
