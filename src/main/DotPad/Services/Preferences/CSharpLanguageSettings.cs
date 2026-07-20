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
}
