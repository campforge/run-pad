namespace DotPad.Avalonia.CodeTextEditor;

// One available quick fix (e.g. "add using") for a given 1-based line. NewCode is the full
// replacement document text — CodeFixMargin applies it directly on click, no incremental edit
// bookkeeping needed for a single-document script editor. UsingsToAdd holds namespaces the fix
// wants imported; this control has no notion of a "project file" to route them to (kept
// dependency-free from DotPad.Contracts/RpadFrontMatter, same as BreakPointMargin), so it just
// raises UsingsToAddRequested for whoever owns that decision (see CodeTextEditor.UsingsToAddRequested).
public sealed record CodeFixEntry(int Line, string Title, string NewCode, IReadOnlyList<string> UsingsToAdd);
