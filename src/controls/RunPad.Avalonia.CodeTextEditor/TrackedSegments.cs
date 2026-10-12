using AvaloniaEdit.Document;

namespace RunPad.Avalonia.CodeTextEditor;

/// <summary>
/// Editor decorations computed in the background against an earlier text (semantic colors, inlay
/// hints, squiggles, occurrence highlights), kept in step with typing: bound to the document, a
/// <see cref="TextSegmentCollection{T}"/> shifts every segment on each edit, so a decoration stays on
/// the same characters until the next analysis replaces it — instead of sitting on stale absolute
/// offsets that land on the wrong characters after every keystroke (the "flicker": wrong colors,
/// jumping hints, then a repaint). Lookups are by range, O(log n), not a scan of every decoration.
/// </summary>
public sealed class TrackedSegments<T> where T : TextSegment
{
    private TextSegmentCollection<T> _segments = [];
    private TextDocument? _document;

    /// <summary>Every segment, in document order, at its current (edit-adjusted) offsets.</summary>
    public IReadOnlyList<T> All => [.. _segments];

    public int Count => _segments.Count;

    /// <summary>
    /// Replaces the decorations. With <paramref name="document"/> they follow its edits from now on;
    /// without one (pure unit tests) they stay where they are. Segments past the document's end are
    /// dropped and ones running over it clipped.
    /// </summary>
    public void Replace(TextDocument? document, IEnumerable<T> segments)
    {
        Disconnect();
        _segments = document is null ? [] : new TextSegmentCollection<T>(document);
        _document = document;
        var length = document?.TextLength ?? int.MaxValue;
        foreach (var segment in segments)
        {
            if (segment.StartOffset < 0 || segment.StartOffset > length) continue;
            if (segment.EndOffset > length) segment.EndOffset = length;
            _segments.Add(segment);
        }
    }

    public void Clear() => Replace(_document, []);

    /// <summary>The segments overlapping [<paramref name="offset"/>, offset + length).</summary>
    public IReadOnlyList<T> Overlapping(int offset, int length) => _segments.FindOverlappingSegments(offset, length);

    /// <summary>The first segment starting at or after <paramref name="offset"/>, or null.</summary>
    public T? FirstStartingAtOrAfter(int offset) => _segments.FindFirstSegmentWithStartAfter(offset);

    private void Disconnect()
    {
        if (_document is not null) _segments.Disconnect(_document);
        _document = null;
    }

    /// <summary>
    /// The span that differs between two decoration lists — what a partial redraw has to cover —
    /// found by skipping the equal head and the equal tail; null when the lists are the same.
    /// Both lists must be in document order.
    /// </summary>
    public static (int Start, int End)? ChangedRange(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, T, bool> same)
    {
        var head = 0;
        while (head < before.Count && head < after.Count && Same(before[head], after[head])) head++;
        if (head == before.Count && head == after.Count) return null;

        var beforeTail = before.Count - 1;
        var afterTail = after.Count - 1;
        while (beforeTail >= head && afterTail >= head && Same(before[beforeTail], after[afterTail]))
        {
            beforeTail--;
            afterTail--;
        }

        var start = int.MaxValue;
        var end = int.MinValue;
        for (var i = head; i <= beforeTail; i++) Include(before[i]);
        for (var i = head; i <= afterTail; i++) Include(after[i]);
        return start <= end ? (start, end) : null;

        bool Same(T a, T b) => a.StartOffset == b.StartOffset && a.Length == b.Length && same(a, b);

        void Include(T segment)
        {
            start = Math.Min(start, segment.StartOffset);
            end = Math.Max(end, segment.EndOffset);
        }
    }
}
