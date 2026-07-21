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

    // Case-insensitive substring exclusion list matched against a fix/refactoring's Title (e.g.
    // "use explicit type", "fix formatting") — lets a user silence specific opinionated suggestions
    // without turning code fixes off entirely. Substring, not exact title: the same provider's title
    // often carries per-candidate specifics (e.g. "Add using System.Text;" vs "Add using
    // System.Text.Json;") that a fixed exact-title list could never fully enumerate.
    public List<string> ExcludedCodeFixTitles { get; set; } = [];
}
