using DotPad.Format.RPad;

namespace DotPad.Contracts.IntelliSense;

public interface IIntelliSenseService : IDisposable
{
    Task<ICompletionList?> GetCompletionsAsync(
        string code,
        int position,
        CompletionTrigger trigger = default,
        CancellationToken cancellationToken = default);

    Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code,
        int position,
        char? triggerCharacter = null,
        CancellationToken cancellationToken = default);

    Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // TODO.md #62 "подсветка вхождений": every occurrence of the symbol at position, elsewhere in
    // the same document (for highlighting them all while the caret sits on one) — null when
    // position isn't on an identifier that resolves to any symbol.
    Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // TODO.md #62 "умное переименование": resolves what an F2 rename at position should do — null
    // when there's nothing renameable there (not on an identifier, or the symbol isn't declared in
    // the user's own script — see IRenameInfo).
    Task<IRenameInfo?> GetRenameInfoAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // TODO.md #62 "Go to Definition" (F12): resolves what F12 at position should do — null when
    // position isn't on an identifier that resolves to any symbol. See IDefinitionResult for the
    // in-source-vs-external split.
    Task<IDefinitionResult?> GetDefinitionAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // TODO.md #62 "Go to Implementation" (Ctrl+F12): every implementation (interface member) or
    // override (abstract/virtual member) of the symbol at position, declared in the script itself —
    // null when position isn't on an identifier, the symbol isn't implementable/overridable, or no
    // implementations exist in this script. See IImplementationLocation.
    Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // Reformats whitespace/indentation using the language's default formatting rules (Roslyn's
    // Formatter for C#). Returns null on failure or if code is already formatted; the caller keeps
    // the original text in that case.
    Task<string?> FormatDocumentAsync(string code, CancellationToken cancellationToken = default);

    // TODO.md #74 "CodeRefactoringProvider хостинг в лампочке" — real Roslyn refactorings (Use
    // Expression Body, Introduce Local, Reverse For Statement, Invert If, ...) available AT position,
    // not tied to any diagnostic the way CodeFixesChanged's fixes are — reuses the same ICodeFixInfo
    // shape (a CodeAction materializes into "Title" + "NewCode" either way) rather than a parallel
    // contract type. Pull-based like GetQuickInfoAsync, not push: the caller re-fetches this on caret
    // move, not on text change — a refactoring's availability depends on WHERE the caret is, which
    // NotifyTextChanged's background pass has no way to know about. Null on failure; empty when
    // nothing applies at position.
    Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // Fires from a background thread. First arg = the code that produced the diagnostics.
    event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;

    // Fires from a background thread. First arg = the code that produced the tokens.
    event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;

    // Fires from a background thread. First arg = the code the fixes were computed against.
    event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;

    // TODO.md #62 "inlay hints" — every inferred `var`/`foreach (var ...)` type annotation to render
    // inline (Rider/VS Code style). Fires from a background thread. First arg = the code the hints
    // were computed against — same push/debounce pattern as SemanticTokensChanged, not a pull-based
    // method, since the editor wants this recomputed on every text change the same way.
    event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged;

    void NotifyTextChanged(string code);

    // Call when the tab's NuGet packages change so IntelliSense sees the new types.
    void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages);

    // Call when the tab's .rpad front-matter `namespaces:` list changes (including once at initial
    // load) so completion/diagnostics/quick-fix ranking see the same global usings the compiler
    // will add — see ICodeFixInfo.UsingsToAdd.
    void UpdateGlobalUsings(IReadOnlyList<string> usings);

    // Call when the tab's .rpad front-matter `kind:` changes (including once at initial load) so live
    // analysis mirrors the same kind-specific transform the compiler applies before running/debugging
    // (e.g. RpadKind.Program's implicit `Main();` call) — otherwise the user's own entry point can be
    // flagged as unused (ANALYSIS.md TODO #66).
    void UpdateKind(RpadKind kind);
}
