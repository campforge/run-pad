using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace DotPad.Avalonia.CodeTextEditor;

internal sealed class BraceFoldingStrategy
{
    public char OpeningBrace { get; set; } = '{';
    public char ClosingBrace { get; set; } = '}';

    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var foldings = CreateFoldings(document.Text);
        manager.UpdateFoldings(foldings, -1);
    }

    private IEnumerable<NewFolding> CreateFoldings(string text)
    {
        var result = new List<NewFolding>();
        var stack = new Stack<int>();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == OpeningBrace)
                stack.Push(i);
            else if (c == ClosingBrace && stack.Count > 0)
            {
                int start = stack.Pop();
                if (i - start > 1)
                    result.Add(new NewFolding(start + 1, i));
            }
        }

        result.Sort(static (a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }
}
