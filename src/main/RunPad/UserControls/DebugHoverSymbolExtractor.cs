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
        return text[start..end];
    }

    private static bool IsIdentifier(char c, IReadOnlySet<char>? extra) => char.IsLetterOrDigit(c) || c == '_' || extra?.Contains(c) == true;
}
