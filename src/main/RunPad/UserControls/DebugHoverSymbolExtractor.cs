namespace RunPad.UserControls;

internal static class DebugHoverSymbolExtractor
{
    public static string? Extract(string text, int offset, IReadOnlySet<char>? extraIdentifierChars = null)
    {
        if (offset < 0 || offset >= text.Length || !IsIdentifier(text[offset], extraIdentifierChars)) return null;
        var start = offset;
        while (start > 0 && IsIdentifier(text[start - 1], extraIdentifierChars)) start--;
        var end = offset + 1;
        while (end < text.Length && IsIdentifier(text[end], extraIdentifierChars)) end++;
        // Extend only across dots that join two identifier segments. This deliberately excludes
        // calls, indexers, assignments, and other arbitrary expressions.
        while (start > 0 && text[start - 1] == '.' && start > 1 && IsIdentifier(text[start - 2], extraIdentifierChars))
        {
            start -= 2;
            while (start > 0 && IsIdentifier(text[start - 1], extraIdentifierChars)) start--;
        }
        while (end < text.Length && text[end] == '.' && end + 1 < text.Length && IsIdentifier(text[end + 1], extraIdentifierChars))
        {
            end += 2;
            while (end < text.Length && IsIdentifier(text[end], extraIdentifierChars)) end++;
        }
        return text[start..end];
    }

    private static bool IsIdentifier(char c, IReadOnlySet<char>? extra) => char.IsLetterOrDigit(c) || c == '_' || extra?.Contains(c) == true;
}
