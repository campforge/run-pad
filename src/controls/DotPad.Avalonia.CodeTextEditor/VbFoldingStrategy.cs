using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

namespace DotPad.Avalonia.CodeTextEditor;

// TODO.md #49 item 3: VB blocks are keyword-delimited (Sub/End Sub, If/End If, ...), not
// brace-delimited, so BraceFoldingStrategy folds nothing at all in a .vb file. Line-based (not
// character-based like BraceFoldingStrategy) since VB's block keywords are only meaningful at the
// start of a line, not as isolated characters — a single flat stack, same "any closer pops whatever
// opener is on top" tolerance BraceFoldingStrategy itself already has (real VB code nests these
// properly; a mismatch only arises for genuinely malformed/still-being-typed code, where an
// imperfect fold region is an acceptable approximation, not a correctness requirement).
public sealed class VbFoldingStrategy : IFoldingStrategy
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

    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var foldings = CreateFoldings(document);
        manager.UpdateFoldings(foldings, -1);
    }

    // internal (not private) so tests can assert on fold regions directly against a plain
    // TextDocument — no FoldingManager/live TextArea needed, same "test the pure computation, not
    // the framework wiring" split already used elsewhere in this project (e.g. CodeTextEditor's own
    // IsWordTriggerContext/GetWordStart).
    internal static IReadOnlyList<NewFolding> CreateFoldings(TextDocument document)
    {
        var result = new List<NewFolding>();
        var openLines = new Stack<DocumentLine>();

        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length).TrimStart();
            if (text.Length == 0) continue;

            // A closer takes priority over an opener when a line could arguably read as both (not
            // reachable with the keyword sets above today, but a closer-first check is the safer
            // default if that ever changes) — "End Sub" must never also be read as opening on "Sub".
            if (ClosingKeyword.IsMatch(text))
            {
                if (openLines.Count == 0) continue;
                var openLine = openLines.Pop();
                if (line.LineNumber - openLine.LineNumber > 0)
                    result.Add(new NewFolding(openLine.EndOffset, line.EndOffset));
            }
            else if (OpeningKeyword.IsMatch(text))
            {
                openLines.Push(line);
            }
        }

        result.Sort(static (a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }
}
