using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using RunPad.Avalonia.CodeTextEditor;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Brace pairs fold only when they span lines: a pair opened and closed on one line has nothing to
// hide and gets no fold marker.
public class BraceFoldingStrategyTests
{
    private static (TextDocument Document, FoldingManager Manager, Func<List<(int Start, int End)>> Lines) Create(string text)
    {
        var editor = new TextEditor { Document = new TextDocument(text) };
        var manager = FoldingManager.Install(editor.TextArea);
        var strategy = new BraceFoldingStrategy();
        var document = editor.Document;
        List<(int, int)> Lines()
        {
            strategy.UpdateFoldings(manager, document);
            return manager.AllFoldings
                .Select(f => (document.GetLineByOffset(f.StartOffset).LineNumber, document.GetLineByOffset(f.EndOffset).LineNumber))
                .ToList();
        }
        return (document, manager, Lines);
    }

    [AvaloniaTheory]
    [InlineData("if (ok) { Run(); }")]
    [InlineData("void Main() { }")]
    [InlineData("var a = new[] { 1, 2 };")]
    [InlineData("{}")]
    public void ASingleLinePair_Should_NotFold(string text)
        => Assert.Empty(Create(text).Lines());

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AMultiLinePair_Should_Fold_ButNotTheSingleLinePairsInside(string newline)
    {
        var text = string.Join(newline,
            "void Main()",
            "{",
            "    if (ok) { Run(); }",
            "    var a = new[] { 1, 2 };",
            "    if (b)",
            "    {",
            "        Run();",
            "    }",
            "}");

        Assert.Equal([(2, 9), (6, 8)], Create(text).Lines());
    }

    [AvaloniaTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AddingOrRemovingANewline_Should_AddOrRemoveTheFold(string newline)
    {
        var (document, _, lines) = Create("void Main() { Run(); }");
        Assert.Empty(lines());

        document.Insert("void Main() {".Length, newline);
        Assert.Equal([(1, 2)], lines());

        document.Remove("void Main() {".Length, newline.Length);
        Assert.Empty(lines());
    }
}
