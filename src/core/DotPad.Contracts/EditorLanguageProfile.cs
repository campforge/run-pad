namespace DotPad.Contracts;

// Which built-in AvaloniaEdit smart-indentation behavior a language wants — kept as an enum rather
// than a concrete AvaloniaEdit type so this contract (like the rest of DotPad.Contracts) doesn't
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

// Which built-in CodeTextEditor code-folding algorithm a language wants — kept as an enum for the
// same UI-framework-independence reason as IndentationStrategyKind; EditorTabItemControl maps it to
// the real DotPad.Avalonia.CodeTextEditor.IFoldingStrategy implementation.
public enum FoldingStrategyKind
{
    // Folds between a language's matching open/close brace characters — correct default for a
    // brace-delimited language (C#'s { }), wrong for a keyword-delimited one (VB's Sub/End Sub).
    BraceBased,

    // Folds between VB's keyword-delimited block pairs (Sub/End Sub, If/End If, For/Next, ...) — see
    // DotPad.Avalonia.CodeTextEditor.VbFoldingStrategy for exactly which constructs (TODO.md #49
    // item 3).
    VbKeywordBased,
}

// The editor-level lexical/syntactic conventions a language plugin wants applied to
// DotPad.Avalonia.CodeTextEditor, which is itself deliberately language-agnostic (see its own XML
// docs on IdentifierChars/ContextualTriggers/WordTriggers/BracePairs/AutoCloseBraces/
// StatementTerminators — all empty-by-default, meant to be configured externally rather than
// assuming any language's rules). Mirrors ILanguageHostFactory.Snippets, which already flows the
// same way from the language plugin to the editor (see TODO.md #41) — this closes the same gap for
// everything CodeTextEditor needs configured (TODO.md #58): before this, EditorTabItemControl
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
    public FoldingStrategyKind FoldingStrategy { get; init; } = FoldingStrategyKind.BraceBased;
}
