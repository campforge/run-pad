namespace DotPad.Contracts.IntelliSense;

// A single quick fix for a diagnostic (currently just "add using"). Line is 1-based, matching the
// editor's other line-indexed concepts (breakpoints, debug current line). NewCode is the full
// document text after applying the fix.
public interface ICodeFixInfo
{
    int Line { get; }
    string Title { get; }
    string NewCode { get; }
}
