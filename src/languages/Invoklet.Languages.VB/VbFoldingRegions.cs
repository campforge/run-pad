using System.Text.RegularExpressions;

namespace Invoklet.Languages.VB;

// Ported from the AvaloniaEdit-coupled VbFoldingStrategy that used to live in
// Invoklet.Avalonia.CodeTextEditor — a language-agnostic control project — and
// deliberately framework-free: Invoklet.Cli references this project directly with no Avalonia
// dependency at all, so this can't take TextDocument/FoldingManager types without dragging the whole
// UI stack along. Plain string in, (Start, End) character-offset pairs out — the same shape
// EditorLanguageProfile.FoldingRegionsProvider expects, and easy to unit-test without any editor
// control at all.
//
// VB blocks are keyword-delimited (Sub/End Sub, If/End If, ...), not brace-delimited, so the
// language-agnostic brace-folding strategy folds nothing at all in a .vb file. Line-based (not
// character-based) since VB's block keywords are only meaningful at the start of a line, not as
// isolated characters — a single flat stack, same "any closer pops whatever opener is on top"
// tolerance the brace-based strategy itself already has (real VB code nests these properly; a
// mismatch only arises for genuinely malformed/still-being-typed code, where an imperfect fold
// region is an acceptable approximation, not a correctness requirement).
public static class VbFoldingRegions
{
    // Deliberately excludes Property — a single-line auto-property ("Public Property Foo As
    // Integer") and a property BLOCK ("Public Property Foo As Integer" ... Get/Set ... "End
    // Property") start with an identical first line, and telling them apart needs a look-ahead this
    // line-at-a-time scan doesn't do; treating every Property line as an opener would produce a
    // bogus, never-closed fold for every auto-property instead. MustOverride/Declare members
    // (interface methods, external DLL declarations) are excluded the same way — they have no body
    // and no matching End — but a plain Interface member with neither modifier (legal VB, the
    // modifier is implicit) isn't detectable from its own line alone; a known, accepted gap, not
    // solved here. "Exit Sub"/"Exit Function" are excluded too — the Sub/Function/... branch matches
    // the keyword anywhere on the line (needed for "Public Shared Sub Foo()"), which would otherwise
    // also catch the unrelated "Exit Sub" early-return statement as if it opened a brand new block.
    // "ElseIf ... Then" needs no separate exclusion despite also ending in "Then" — every branch
    // except the Sub/Function/... one is anchored at the very start of the line (right after the
    // shared "^(?!End\b)"), and "ElseIf" doesn't itself start with "If", so it can never match the
    // If-block branch at all; it continues the same fold the original "If" opened, same as any other
    // ordinary line in the block.
    private static readonly Regex OpeningKeyword = new(
        @"^(?!End\b)(?:If\b.*\bThen\s*(?:'.*)?$" +
        @"|For\b" +
        @"|While\b" +
        @"|Do\b" +
        @"|Select\s+Case\b" +
        @"|Try\s*(?:'.*)?$" +
        @"|With\b" +
        @"|(?!.*\b(?:MustOverride|Declare|Exit)\b).*\b(?:Sub|Function|Class|Module|Structure|Interface|Enum|Namespace)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClosingKeyword = new(
        @"^(?:End\s+If\b|Next\b|End\s+While\b|Wend\b|Loop\b|End\s+Select\b|End\s+Try\b|End\s+With\b" +
        @"|End\s+(?:Sub|Function|Class|Module|Structure|Interface|Enum|Namespace)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<(int Start, int End)> Compute(string text)
    {
        var result = new List<(int Start, int End)>();
        var openLines = new Stack<(int LineNumber, int EndOffset)>();

        var lineNumber = 0;
        var lineStart = 0;
        while (true)
        {
            lineNumber++;
            var newlineIndex = text.IndexOf('\n', lineStart);
            var lineEnd = newlineIndex == -1 ? text.Length
                : newlineIndex > lineStart && text[newlineIndex - 1] == '\r' ? newlineIndex - 1 : newlineIndex;

            var lineText = text[lineStart..lineEnd].TrimStart();
            if (lineText.Length > 0)
            {
                // A closer takes priority over an opener when a line could arguably read as both
                // (not reachable with the keyword sets above today, but a closer-first check is the
                // safer default if that ever changes) — "End Sub" must never also be read as opening
                // on "Sub".
                if (ClosingKeyword.IsMatch(lineText))
                {
                    if (openLines.Count > 0)
                    {
                        var (openLineNumber, openEndOffset) = openLines.Pop();
                        if (lineNumber - openLineNumber > 0)
                            result.Add((openEndOffset, lineEnd));
                    }
                }
                else if (OpeningKeyword.IsMatch(lineText))
                {
                    openLines.Push((lineNumber, lineEnd));
                }
            }

            if (newlineIndex == -1) break;
            lineStart = newlineIndex + 1;
        }

        result.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return result;
    }
}
