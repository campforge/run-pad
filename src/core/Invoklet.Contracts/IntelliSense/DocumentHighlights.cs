namespace Invoklet.Contracts.IntelliSense;

// One occurrence of the symbol under the caret, elsewhere in the
// same document — Start/Length are 0-based offsets/lengths into the caller's own code string (the
// same coordinate space every other IIntelliSenseService method already uses), not compiled-file
// coordinates.
public interface IDocumentHighlightSpan
{
    int Start { get; }
    int Length { get; }
}

// What a rename-symbol request at a caret
// position resolves to — the symbol's own current name (to pre-fill a rename UI) plus every span
// in the document that needs to be replaced with the new name (its declaration + every reference,
// the same set GetDocumentHighlightsAsync would return, but only for symbols the user's own script
// actually declares — see the service implementation for why metadata/framework symbols are
// excluded).
public interface IRenameInfo
{
    string CurrentName { get; }
    IReadOnlyList<IDocumentHighlightSpan> Spans { get; }
}
