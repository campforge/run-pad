namespace DotPad.Contracts.IntelliSense;

// TODO.md #62 "подсветка вхождений": one occurrence of the symbol under the caret, elsewhere in the
// same document — Start/Length are 0-based offsets/lengths into the caller's own code string (the
// same coordinate space every other IIntelliSenseService method already uses), not compiled-file
// coordinates.
public interface IDocumentHighlightSpan
{
    int Start { get; }
    int Length { get; }
}
