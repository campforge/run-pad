using DotPad.Contracts;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.VB;

public sealed class VbLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public VbLanguageHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public string Language => "vb";
    public string DisplayName => "Visual Basic";

    // No IntelliSense flag yet — VB tabs compile/run/debug but have no autocomplete/diagnostics/
    // quick-fixes (would need a VisualBasicIntelliSenseService against Microsoft.CodeAnalysis.VisualBasic).
    public LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump;

    // No VB snippets defined yet — same gap as IntelliSense above.
    public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();

    // VB's own lexical shape, not C#'s: blocks are `Sub`/`End Sub`, `If`/`End If`, `Class`/`End
    // Class` — no braces at all, so brace-match/auto-close/fold only make sense for the parens and
    // array-literal brackets VB does share with C#. Generic type arguments use "Of", not a bare '<'
    // (which is also VB's comparison operator, same reasoning C# excludes it from
    // CompletionTriggers). No required statement terminator — a newline ends a statement. VB is
    // case-insensitive, so "New"/"new" both starting completion is a real language property, not
    // sloppiness, hence the case-insensitive HashSet.
    public EditorLanguageProfile EditorProfile { get; } = new()
    {
        CompletionTriggers = new HashSet<char> { '.' },
        OverloadTriggers = new HashSet<char> { '(', ',' },
        IdentifierChars = new HashSet<char> { '_' },
        ContextualTriggers = new HashSet<char>(),
        WordTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New" },
        StatementTerminators = new HashSet<char>(),
        BracePairs = new Dictionary<char, char> { ['('] = ')', ['['] = ']' },
        AutoCloseBraces = new HashSet<char> { '[' },
        IndentationStrategy = IndentationStrategyKind.Default,
        // TODO.md #49 item 3 / #76 — VB's blocks aren't brace-delimited, so the default brace-based
        // folding strategy folded nothing at all in a .vb file. VbFoldingRegions.Compute lives right
        // here in the VB language project, not in the language-agnostic editor control (TODO.md #76).
        FoldingRegionsProvider = VbFoldingRegions.Compute,
    };

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as VbLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(VbLanguageHostOptions)}.", nameof(options));
        return new VbLanguageHost(opts, _loggerFactory);
    }
}
