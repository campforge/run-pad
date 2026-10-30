using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace Invoklet.Avalonia.CodeTextEditor.Folding;

// A language's code-folding algorithm — CodeTextEditor itself stays language-agnostic (see its own
// XML docs on BracePairs/WordTriggers/etc.), so which strategy applies is configured externally
// (folding used to be hardcoded to BraceFoldingStrategy for every language,
// including VB, whose blocks aren't brace-delimited at all).
public interface IFoldingStrategy
{
    void UpdateFoldings(FoldingManager manager, TextDocument document);
}
