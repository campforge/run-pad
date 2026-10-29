namespace Invoklet.Avalonia.CodeTextEditor;

/// <summary>
/// Comments or uncomments the lines a selection touches with a language's line-comment prefix
/// (<c>//</c> in C#, <c>'</c> in Visual Basic). Pure text arithmetic, so the rules can be checked
/// without an editor: <see cref="Compute"/> only reports the edits, the caller applies them.
/// </summary>
/// <remarks>
/// A line counts as touched when the selection covers any of it, except a last line the selection
/// only reaches at its very start (a selection of whole lines ends there). With no selection the
/// caret's line is the one touched. Blank lines are left alone. When every other touched line
/// already starts with the prefix after its indentation, one prefix (and one space after it) is
/// removed from each; otherwise the prefix and a space are added after each line's indentation.
/// Only text inside lines changes, so line endings stay as they are.
/// </remarks>
public static class LineCommentToggle
{
    /// <summary>Replace <paramref name="RemoveLength"/> characters at <paramref name="Offset"/>
    /// with <paramref name="Insert"/>.</summary>
    public readonly record struct Edit(int Offset, int RemoveLength, string Insert);

    /// <summary>The edits that toggle comments on the lines touched by the range
    /// [<paramref name="selectionStart"/>, <paramref name="selectionEnd"/>], in ascending offset
    /// order; empty when there is no prefix or no line with code.</summary>
    public static IReadOnlyList<Edit> Compute(string text, int selectionStart, int selectionEnd, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];
        if (selectionEnd < selectionStart) (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionEnd = Math.Clamp(selectionEnd, 0, text.Length);

        var lines = Lines(text);
        var first = LineAt(lines, selectionStart);
        var last = LineAt(lines, selectionEnd);
        if (last > first && selectionEnd == lines[last].Start) last--;

        var code = new List<(int Start, int Indent, int End)>();
        for (var i = first; i <= last; i++)
        {
            var (start, end) = lines[i];
            var indent = start;
            while (indent < end && text[indent] is ' ' or '\t') indent++;
            if (indent < end) code.Add((start, indent, end));
        }
        if (code.Count == 0) return [];

        var commented = code.TrueForAll(l => string.CompareOrdinal(text, l.Indent, prefix, 0, prefix.Length) == 0
            && l.End - l.Indent >= prefix.Length);
        var edits = new List<Edit>(code.Count);
        foreach (var (_, indent, end) in code)
        {
            if (!commented)
            {
                edits.Add(new Edit(indent, 0, prefix + " "));
                continue;
            }
            var length = prefix.Length;
            if (indent + length < end && text[indent + length] == ' ') length++;
            edits.Add(new Edit(indent, length, ""));
        }
        return edits;
    }

    /// <summary>Applies <paramref name="edits"/> (as returned by <see cref="Compute"/>) to
    /// <paramref name="text"/>.</summary>
    public static string Apply(string text, IReadOnlyList<Edit> edits)
    {
        var builder = new System.Text.StringBuilder(text);
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var edit = edits[i];
            builder.Remove(edit.Offset, edit.RemoveLength).Insert(edit.Offset, edit.Insert);
        }
        return builder.ToString();
    }

    // Each line's content as [Start, End), without its terminator (CRLF, LF or CR).
    private static List<(int Start, int End)> Lines(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            lines.Add((start, i));
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add((start, text.Length));
        return lines;
    }

    private static int LineAt(List<(int Start, int End)> lines, int offset)
    {
        var line = 0;
        while (line + 1 < lines.Count && lines[line + 1].Start <= offset) line++;
        return line;
    }
}
