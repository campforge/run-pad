using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace Invoklet.Avalonia.CodeTextEditor;

// Replaces the VB-specific VbFoldingStrategy that used to live here — a
// language-agnostic control project directly hosting one particular language's folding regexes.
// This control stays language-agnostic by wrapping WHATEVER offset-pair-producing function a
// language plugin supplies (EditorLanguageProfile.FoldingRegionsProvider) instead of knowing about
// any specific language's folding rules itself; the actual algorithm now lives in the language
// project that owns it (e.g. Invoklet.Languages.VB.VbFoldingRegions).
public sealed class DelegateFoldingStrategy : IFoldingStrategy
{
    private readonly Func<string, IReadOnlyList<(int Start, int End)>> _computeFoldRegions;

    public DelegateFoldingStrategy(Func<string, IReadOnlyList<(int Start, int End)>> computeFoldRegions)
    {
        _computeFoldRegions = computeFoldRegions;
    }

    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var foldings = _computeFoldRegions(document.Text)
            .Select(r => new NewFolding(r.Start, r.End))
            .ToList();
        manager.UpdateFoldings(foldings, -1);
    }
}
