namespace RunPad.UserControls;

internal static class DebugHoverSymbolExtractor
{
    public static string? Extract(string text, int offset, IReadOnlySet<char>? extraIdentifierChars = null)
        => Extract(text, offset, extraIdentifierChars, out _);

    public static string? Extract(string text, int offset, IReadOnlySet<char>? extraIdentifierChars, out int start)
    {
        start = offset;
        if (offset < 0 || offset >= text.Length || !IsIdentifier(text[offset], extraIdentifierChars)) return null;
        while (start > 0 && IsIdentifier(text[start - 1], extraIdentifierChars)) start--;
        var end = offset + 1;
        while (end < text.Length && IsIdentifier(text[end], extraIdentifierChars)) end++;
        // Extend only to the left, across dots that join two identifier segments: the hovered segment
        // ends the expression ("c" in "c.Dump()" is c, "Name" in "c.Name" is c.Name), as in the
        // Visual Studio debugger. This deliberately excludes calls, indexers, assignments, and other
        // arbitrary expressions.
        while (start > 1 && text[start - 1] == '.' && IsIdentifier(text[start - 2], extraIdentifierChars))
        {
            start -= 2;
            while (start > 0 && IsIdentifier(text[start - 1], extraIdentifierChars)) start--;
        }
        return text[start..end];
    }

    private static bool IsIdentifier(char c, IReadOnlySet<char>? extra) => char.IsLetterOrDigit(c) || c == '_' || extra?.Contains(c) == true;
}
