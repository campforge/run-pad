using AvaloniaEdit.Document;
using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO.md #49 item 3: BraceFoldingStrategy folds nothing at all in a .vb file, since VB's
// blocks are keyword-delimited (Sub/End Sub, If/End If, ...), not brace-delimited. Tests go straight
// at VbFoldingStrategy.CreateFoldings — a plain TextDocument in, fold regions out — no
// FoldingManager/live TextArea needed, same split already used for CodeTextEditor's own pure
// position-based checks.
public class VbFoldingStrategyTests
{
    private static IReadOnlyList<(int StartLine, int EndLine)> GetFoldedLineRanges(string code)
    {
        var document = new TextDocument(code);
        return VbFoldingStrategy.CreateFoldings(document)
            .Select(f => (document.GetLineByOffset(f.StartOffset).LineNumber,
                document.GetLineByOffset(f.EndOffset).LineNumber))
            .ToList();
    }

    [Fact]
    public void CreateFoldings_Should_FoldASub()
    {
        const string code = "Public Sub Foo()\n    Console.WriteLine(1)\nEnd Sub\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_FoldAFunction()
    {
        const string code = "Public Function Foo() As Integer\n    Return 1\nEnd Function\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_FoldNestedBlocks()
    {
        const string code =
            "Public Class C\n" +
            "    Public Sub Foo()\n" +
            "        If True Then\n" +
            "            Console.WriteLine(1)\n" +
            "        End If\n" +
            "    End Sub\n" +
            "End Class\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(3, 5), (2, 6), (1, 7)], folds.OrderBy(f => f.EndLine - f.StartLine).ToList());
    }

    [Fact]
    public void CreateFoldings_Should_FoldForNext()
    {
        const string code = "For i = 1 To 10\n    Console.WriteLine(i)\nNext i\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_FoldDoLoop()
    {
        const string code = "Do While x < 10\n    x += 1\nLoop\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_FoldSelectCase()
    {
        const string code = "Select Case x\n    Case 1\n        Foo()\nEnd Select\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 4)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_FoldTryCatch()
    {
        const string code = "Try\n    Foo()\nCatch ex As Exception\n    Bar()\nEnd Try\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 5)], folds);
    }

    // Regression guard: an auto-property ("Public Property Foo As Integer") must not produce a
    // never-closed fold — Property is deliberately excluded from the opening-keyword set.
    [Fact]
    public void CreateFoldings_Should_NotFold_AnAutoProperty()
    {
        const string code = "Public Class C\n    Public Property Foo As Integer\nEnd Class\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    // Regression guard: "Exit Sub" must not be read as opening a second, unrelated block — it would
    // otherwise desynchronize every fold region for the rest of the file.
    [Fact]
    public void CreateFoldings_Should_NotTreatExitSub_AsANewOpener()
    {
        const string code =
            "Public Sub Foo()\n" +
            "    If True Then\n" +
            "        Exit Sub\n" +
            "    End If\n" +
            "End Sub\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(2, 4), (1, 5)], folds.OrderBy(f => f.EndLine - f.StartLine).ToList());
    }

    // Regression guard: "ElseIf ... Then" continues the SAME If block, it must not be read as
    // starting a brand new one.
    [Fact]
    public void CreateFoldings_Should_TreatElseIf_AsPartOfTheSameIfBlock()
    {
        const string code =
            "If x = 1 Then\n" +
            "    Foo()\n" +
            "ElseIf x = 2 Then\n" +
            "    Bar()\n" +
            "End If\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 5)], folds);
    }

    // Regression guard: a single-line If ("If x Then Foo()") has no matching End If and must not
    // open a fold at all.
    [Fact]
    public void CreateFoldings_Should_NotFold_ASingleLineIf()
    {
        const string code = "Public Sub Foo()\n    If True Then Console.WriteLine(1)\nEnd Sub\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Equal([(1, 3)], folds);
    }

    [Fact]
    public void CreateFoldings_Should_ReturnNothing_ForCodeWithNoBlocks()
    {
        const string code = "Dim x = 1\nConsole.WriteLine(x)\n";

        var folds = GetFoldedLineRanges(code);

        Assert.Empty(folds);
    }
}
