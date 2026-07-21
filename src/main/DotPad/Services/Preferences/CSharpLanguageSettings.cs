namespace DotPad.Services.Preferences;

// A settings bag per language that has its own settings to hold, same shape as DebuggerSettings —
// not a generic "per-language settings" framework (TODO.md #62/"Реорганизация Preferences" — that's
// a bigger, still-open task; this is just the first concrete language settings bag, for the one
// language that has a setting worth exposing today).
public sealed class CSharpLanguageSettings
{
    // TODO.md #62 "inlay hints" — inferred `var x`/`foreach (var x in ...)` type annotations
    // (Rider/VS Code style). Global switch, same rationale as EditorSettings.CommitCharactersEnabled
    // (TODO.md #60.4) — some users find inline decorations distracting and want them off entirely.
    public bool InlayHintsEnabled { get; set; } = true;

    // TODO.md #74 (user request 2026-07-21) — master switch for the lightbulb margin's Roslyn code
    // fixes/refactorings (RoslynCodeFixEngine). Diagnostics themselves (the squiggles) are unaffected
    // — this only gates the FIX suggestions layered on top, for users who find the lightbulb noisy.
    public bool CodeFixesEnabled { get; set; } = true;

    // TODO.md #74 (сто шестой проход, user request 2026-07-21) — exact, case-insensitive Roslyn
    // diagnostic ID exclusion list ("CS0184", "IDE0007", "IDE0055", ...) — lets a user silence
    // specific opinionated suggestions without turning code fixes off entirely. A diagnostic ID
    // instead of a Title substring (сто пятый проход's original design): stable and documented,
    // where a title is free-form prose the provider can phrase however it likes. Only applies to
    // diagnostic-driven fixes — a CodeRefactoringProvider's suggestion has no underlying diagnostic
    // to key off (ICodeFixInfo.DiagnosticId's own doc comment), so it can't be excluded this way.
    //
    // IDE0055 ("Fix formatting") ships excluded by default (user request 2026-07-21): DotPad already
    // has its own `dotnet format`-based CI gate and editor formatting (Ctrl+Format), so a live
    // "reformat this" suggestion firing on every whitespace deviation while typing was judged more
    // noise than help for most users — same rationale as InlayHintsEnabled/CodeFixesEnabled being
    // opt-OUT rather than opt-IN elsewhere in this file: default to what most users want, not to the
    // maximal feature set.
    public List<string> ExcludedDiagnosticIds { get; set; } = ["IDE0055"];
}
