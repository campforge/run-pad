namespace DotPad.Avalonia.CodeTextEditor;

// One available quick fix (e.g. "add using") for a given 1-based line. NewCode is the full
// replacement document text — CodeFixMargin applies it directly on click, no incremental edit
// bookkeeping needed for a single-document script editor.
public sealed record CodeFixEntry(int Line, string Title, string NewCode);
