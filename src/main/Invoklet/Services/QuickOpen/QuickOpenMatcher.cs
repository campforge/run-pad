namespace Invoklet.Services.QuickOpen;

/// <summary>
/// Orders the scripts for what the user has typed: the name first (the whole name, its beginning, the starts of its words, a piece of
/// it, its letters in order), the folder only as a last resort, and recently opened files ahead of equal matches. With nothing typed
/// the recent files come first, most recent first, then the rest by name.
/// </summary>
public static class QuickOpenMatcher
{
    public const int DefaultLimit = 50;

    private const int Exact = 10_000;
    private const int Prefix = 8_000;
    private const int WordStarts = 6_000;
    private const int Substring = 4_000;
    private const int Letters = 2_000;
    private const int InFolder = 500;

    public static IReadOnlyList<QuickOpenEntry> Rank(IEnumerable<QuickOpenEntry> entries, string query, int limit = DefaultLimit)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [.. entries
                .OrderBy(e => e.RecentRank < 0 ? int.MaxValue : e.RecentRank)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Target, StringComparer.OrdinalIgnoreCase)
                .Take(limit)];
        }

        return [.. entries
            .Select(e => (Entry: e, Score: Score(e, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entry.Target, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .Take(limit)];
    }

    private static int Score(QuickOpenEntry entry, string query)
    {
        var match = NameScore(entry.Name, query);
        if (match == 0 && IsSubsequence(entry.Folder + "/" + entry.Name, query) >= 0) match = InFolder;
        if (match == 0) return 0;
        // Recent files win between matches of the same kind, never across kinds.
        var recent = entry.RecentRank < 0 ? 0 : Math.Max(0, 100 - entry.RecentRank);
        return match + recent;
    }

    private static int NameScore(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return Exact;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return Prefix;
        if (MatchesWordStarts(name, query)) return WordStarts;
        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return Substring - at;
        var gaps = IsSubsequence(name, query);
        return gaps >= 0 ? Letters - gaps : 0;
    }

    // Each typed letter, in order, is the first letter of a word of the name (a capital after a lower-case letter, or a letter after a
    // separator): "mfr" finds "MyFirstReport" and "my_first_report".
    private static bool MatchesWordStarts(string name, string query)
    {
        var q = 0;
        for (var i = 0; i < name.Length && q < query.Length; i++)
            if (IsWordStart(name, i) && char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(query[q])) q++;
        return q == query.Length;
    }

    private static bool IsWordStart(string name, int i)
        => i == 0 || !char.IsLetterOrDigit(name[i - 1]) && char.IsLetterOrDigit(name[i])
            || char.IsUpper(name[i]) && char.IsLower(name[i - 1]);

    // The letters of the query in order anywhere in the text: the number of letters skipped between them, or -1 when they are not all there.
    private static int IsSubsequence(string text, string query)
    {
        var q = 0;
        var gaps = 0;
        var last = -1;
        for (var i = 0; i < text.Length && q < query.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(query[q])) continue;
            if (last >= 0) gaps += i - last - 1;
            last = i;
            q++;
        }
        return q == query.Length ? Math.Min(gaps, 1_000) : -1;
    }
}
