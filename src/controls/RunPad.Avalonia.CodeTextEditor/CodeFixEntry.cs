namespace RunPad.Avalonia.CodeTextEditor;

// Mirrors RunPad.Contracts.IntelliSense.DiagnosticSeverity — duplicated rather than referenced so
// this control stays dependency-free from RunPad.Contracts (see CodeFixEntry's own doc comment
// below). Lets CodeFixMargin color the lightbulb by how serious the underlying
// diagnostic is instead of one neutral color for every fix.
public enum CodeFixSeverity
{
    Info,
    Warning,
    Error,
}

// One available quick fix/
// refactoring for a given 1-based line. Deliberately carries no applied text: materializing that
// (running the fix provider's own document transform, then diffing it) turned out to be the
// dominant cost in the whole IntelliSense pipeline when done eagerly for every candidate on every
// keystroke/caret-move — see RunPad.Contracts.IntelliSense.ICodeFixInfo's own doc comment for the
// full story. Key is an opaque handle CodeFixMargin hands back via FixApplyRequested when the user
// actually clicks this specific entry — this control has no notion of Roslyn/IIntelliSenseService
// (kept dependency-free from RunPad.Contracts, same as BreakPointMargin), so materializing/applying
// it is entirely the click-time responsibility of whoever owns that (CodeTextEditor.
// FixApplyRequested → EditorTabItemControl). DiagnosticId mirrors ICodeFixInfo.DiagnosticId — a
// plain nullable string, same as Title/Key, so carrying it doesn't add a RunPad.Contracts dependency
// — it's null for a caret-position-triggered refactoring, which has no underlying diagnostic to key
// off. Defaults to null rather than being required —
// most call sites (every test that isn't specifically about ID-based exclusion filtering) don't care
// about it, same rationale as ICodeFixInfo's real implementation defaulting it for refactorings.
public sealed record CodeFixEntry(int Line, string Title, CodeFixSeverity Severity, string Key, string? DiagnosticId = null);
